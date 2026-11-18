// Segment bookkeeping for multi-connection downloads. Ranges are inclusive: [start, end].
// A segment is { start, end, received } where `received` counts bytes written from `start`.
// While a download runs, segments also carry runtime fields: `speed` (measured bytes/s),
// `inflight` (bytes being written right now) and the worker's abort controller.

export const MIN_SEGMENT = 1024 * 1024; // smallest initial slice worth its own connection
export const MIN_PIECE = 32 * 1024; // smallest tail worth handing to another connection

export const segmentLength = (s) => s.end - s.start + 1;
export const remaining = (s) => Math.max(0, segmentLength(s) - s.received);
export const isDone = (s) => remaining(s) === 0;
export const receivedBytes = (segments) => segments.reduce((sum, s) => sum + Math.min(s.received, segmentLength(s)), 0);

/** Divide `size` bytes into at most `connections` roughly equal segments. */
export function planSegments(size, connections, minSegment = MIN_SEGMENT) {
  if (!size || size <= 0) return [];
  const count = Math.max(1, Math.min(connections, Math.floor(size / minSegment) || 1));
  const base = Math.floor(size / count);
  const segments = [];
  let start = 0;
  for (let i = 0; i < count; i++) {
    const end = i === count - 1 ? size - 1 : start + base - 1;
    segments.push({ start, end, received: 0 });
    start = end + 1;
  }
  return segments;
}

/**
 * Speed-aware dynamic segmentation for an idle connection.
 *
 * Picks the active segment expected to finish last (remaining bytes / its measured
 * speed) and moves its tail to a new connection. The cut is placed so both are
 * expected to finish together, accounting for the new connection's start-up time:
 *   keep / slowSpeed = startup + (free - keep) / helperSpeed
 * A slow or stalled connection therefore keeps almost nothing, while an average one
 * keeps about half. Returns { piece, source }, or null when a new connection would
 * not finish meaningfully sooner. Segments without a measured `speed` are assumed
 * to run at `helperSpeed`.
 */
export function splitForHelper(candidates, { helperSpeed, startupSeconds = 0.4, minPiece = MIN_PIECE } = {}) {
  const fast = Math.max(helperSpeed || 0, 1);
  let source = null;
  let latest = 0;
  for (const s of candidates) {
    const free = remaining(s) - (s.inflight ?? 0);
    if (free < 2 * minPiece) continue;
    const eta = free / Math.max(s.speed ?? fast, 1);
    if (eta > latest) {
      source = s;
      latest = eta;
    }
  }
  if (!source) return null;

  const free = remaining(source) - (source.inflight ?? 0);
  const slow = Math.max(source.speed ?? fast, 0);
  let keep = slow > 0 ? Math.round((slow * (startupSeconds * fast + free)) / (slow + fast)) : 0;
  keep = Math.max(0, Math.min(keep, free - minPiece));
  const alone = slow > 0 ? free / slow : Infinity;
  const together = startupSeconds + (free - keep) / fast;
  if (together >= alone * 0.8) return null;

  const cut = source.start + source.received + (source.inflight ?? 0) + keep;
  const piece = { start: cut, end: source.end, received: 0 };
  source.end = cut - 1;
  return { piece, source };
}

/** Insert keeping the list ordered by start offset (nicer progress maps). */
export function insertSegment(segments, piece) {
  const index = segments.findIndex((s) => s.start > piece.start);
  if (index === -1) segments.push(piece);
  else segments.splice(index, 0, piece);
  return piece;
}

/** Plain data for persistence and the UI (drops runtime-only fields). */
export const serializeSegments = (segments) => segments.map(({ start, end, received }) => ({ start, end, received }));

/** Validate segments loaded from a resume file against the expected size. */
export function validSegments(segments, size) {
  if (!Array.isArray(segments) || !segments.length) return false;
  let expected = 0;
  for (const s of [...segments].sort((a, b) => a.start - b.start)) {
    if (!Number.isInteger(s.start) || !Number.isInteger(s.end) || !Number.isInteger(s.received)) return false;
    if (s.start !== expected || s.end < s.start || s.received < 0 || s.received > segmentLength(s)) return false;
    expected = s.end + 1;
  }
  return expected === size;
}
