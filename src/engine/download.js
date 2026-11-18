// One download: probe, segmented (or single-stream) transfer, pause/resume, crash-safe progress.
import { EventEmitter, setMaxListeners } from "node:events";
import { randomUUID } from "node:crypto";
import { mkdir, open, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { HttpError, isRetryable, probe, request, sanitizeFileName } from "./http-client.js";
import { Throttle, sleep } from "./throttle.js";
import {
  insertSegment, isDone, planSegments, receivedBytes, serializeSegments, splitForHelper, validSegments,
} from "./segments.js";

const MAX_RETRIES = 8; // consecutive failures without progress before a segment gives up
const SAVE_INTERVAL_MS = 2000;
const STALL_MS = 6000; // reconnect a connection that delivered nothing this long while others flow
const RECOVER_MS = 10_000; // after a server refused extra connections, try one more this often
const MIN_HELPER_SPEED = 32 * 1024;
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, Math.round(Number(n) || lo)));
const exists = (path) => stat(path).then(() => true, () => false);
const fail = (message, retryable) => Object.assign(new Error(message), { retryable });

export class Download extends EventEmitter {
  #abort = null;
  #running = null;
  #cancelRequested = false;
  #reschedule = null;
  #active = new Set();
  #limit = 1;
  #windowBytes = 0;
  #lastTick = 0;
  #lastSave = 0;
  #saving = Promise.resolve();
  #connSpeed = 0; // typical per-connection speed, used to plan takeovers
  #lastCappedAt = 0;

  constructor(options = {}, { globalThrottle = null } = {}) {
    super();
    this.id = options.id ?? randomUUID();
    this.url = String(options.url);
    this.dir = options.dir;
    this.fileName = options.fileName ?? null; // fixed once the .part file is claimed
    this.suggestedName = options.suggestedName ? sanitizeFileName(options.suggestedName) : null;
    this.connections = clamp(options.connections ?? 8, 1, 32);
    this.headers = options.headers ?? {}; // cookie / referer / user-agent handed over by the browser
    this.limiter = new Throttle(options.speedLimit ?? 0);
    this.globalThrottle = globalThrottle;
    this.status = options.status ?? "queued";
    this.size = options.size ?? null;
    this.received = options.received ?? 0;
    this.resumable = options.resumable ?? false;
    this.error = options.error ?? null;
    this.createdAt = options.createdAt ?? Date.now();
    this.completedAt = options.completedAt ?? null;
    this.finalUrl = null;
    this.etag = null;
    this.lastModified = null;
    this.validator = null;
    this.segments = [];
    this.speed = 0;
  }

  get targetPath() { return join(this.dir, this.fileName); }
  get partPath() { return `${this.targetPath}.part`; }
  get metaPath() { return `${this.targetPath}.part.dsm`; }
  get isRunning() { return this.#running !== null; }

  start() {
    if (this.#running) return this.#running;
    if (this.status === "completed") return Promise.resolve();
    this.#cancelRequested = false;
    this.error = null;
    this.#abort = new AbortController();
    this.#running = this.#run(this.#abort.signal).finally(() => { this.#running = null; });
    return this.#running;
  }

  /** Mark as waiting for a free slot; the manager starts queued downloads in order. */
  queue() {
    if (!this.#running && this.status !== "completed") this.#setStatus("queued");
  }

  async pause() {
    if (this.#running) {
      this.#abort.abort();
      await this.#running;
    } else if (this.status === "queued" || this.status === "error") {
      this.#setStatus("paused");
    }
  }

  async cancel() {
    if (this.status === "completed") return;
    this.#cancelRequested = true;
    if (this.#running) {
      this.#abort.abort();
      await this.#running;
    }
    if (this.fileName) {
      await rm(this.partPath, { force: true });
      await rm(this.metaPath, { force: true });
    }
    this.#setStatus("cancelled");
  }

  setSpeedLimit(bytesPerSecond) {
    this.limiter.setRate(bytesPerSecond);
  }

  setConnections(count) {
    this.connections = clamp(count, 1, 32);
    this.#limit = this.connections;
    this.#reschedule?.();
  }

  toJSON() {
    const eta = this.size !== null && this.speed > 1 ? (this.size - this.received) / this.speed : null;
    return {
      id: this.id,
      url: this.url,
      fileName: this.fileName ?? this.suggestedName,
      dir: this.dir,
      status: this.status,
      size: this.size,
      received: this.received,
      speed: Math.round(this.speed),
      eta: eta === null ? null : Math.round(eta),
      connections: this.connections,
      activeConnections: this.#active.size,
      speedLimit: this.limiter.rate,
      resumable: this.resumable,
      error: this.error,
      createdAt: this.createdAt,
      completedAt: this.completedAt,
      segments: this.resumable ? serializeSegments(this.segments) : [],
    };
  }

  /** What the manager persists to recreate this download after a restart. */
  toRecord() {
    const { segments, speed, eta, activeConnections, ...rest } = this.toJSON();
    return { ...rest, fileName: this.fileName, suggestedName: this.suggestedName, headers: this.headers };
  }

  async #run(signal) {
    this.#lastTick = Date.now();
    const ticker = setInterval(() => this.#tick(), 1000);
    try {
      this.#setStatus("connecting");
      const info = await probe(this.url, { headers: this.headers, signal });
      this.finalUrl = info.finalUrl;
      this.size = info.size;
      this.resumable = info.resumable && info.size !== null;
      this.etag = info.etag;
      this.lastModified = info.lastModified;
      this.validator = info.etag && !info.etag.startsWith("W/") ? info.etag : info.lastModified;
      await mkdir(this.dir, { recursive: true });

      let fh;
      let segments = null;
      if (this.fileName) {
        segments = this.resumable ? await this.#loadSegments() : null;
        fh = await open(this.partPath, segments ? "r+" : "w+");
      } else {
        ({ name: this.fileName, fh } = await claimName(this.dir, this.suggestedName ?? info.fileName));
      }

      try {
        if (this.resumable) {
          if (!segments) {
            await fh.truncate(this.size); // pre-allocate; every segment writes at its own offset
            segments = planSegments(this.size, this.connections);
          }
          this.segments = segments;
          this.received = receivedBytes(segments);
          this.#setStatus("downloading");
          await this.#saveState(true);
          await this.#runSegmented(fh, signal);
        } else {
          this.segments = [];
          this.#setStatus("downloading");
          await this.#runSingle(fh, signal);
        }
      } finally {
        await fh.close();
      }

      await this.#saving;
      await rename(this.partPath, this.targetPath);
      await rm(this.metaPath, { force: true });
      this.completedAt = Date.now();
      this.#setStatus("completed");
    } catch (err) {
      if (!signal.aborted) this.error = err?.message ?? String(err);
      await this.#saveState(true);
      this.#setStatus(signal.aborted ? (this.#cancelRequested ? "cancelled" : "paused") : "error");
    } finally {
      clearInterval(ticker);
      this.speed = 0;
    }
  }

  /** Keep up to `connections` workers busy; split the largest segment whenever one frees up. */
  async #runSegmented(fh, outerSignal) {
    const workers = new AbortController();
    const onAbort = () => workers.abort(outerSignal.reason);
    outerSignal.addEventListener("abort", onAbort, { once: true });
    const signal = workers.signal;
    setMaxListeners(128, signal); // every connection's request and throttle wait listens on it
    this.#limit = this.connections;
    this.#connSpeed = 0;
    let fatal = null;

    try {
      await new Promise((resolve) => {
        const launch = (seg) => {
          seg.speed = undefined;
          seg.windowBytes = 0;
          seg.activeSince = Date.now();
          this.#active.add(seg);
          this.#worker(seg, fh, signal)
            .catch((err) => {
              if (!signal.aborted) {
                fatal = err;
                workers.abort(err);
              }
            })
            .finally(() => {
              this.#active.delete(seg);
              schedule();
            });
        };
        const schedule = () => {
          if (!signal.aborted) {
            for (const seg of this.segments) {
              if (this.#active.size >= this.#limit) break;
              if (!isDone(seg) && !this.#active.has(seg)) launch(seg);
            }
            while (this.#active.size < this.#limit) {
              const split = splitForHelper([...this.#active], { helperSpeed: this.#helperSpeed() });
              if (!split) break;
              launch(insertSegment(this.segments, split.piece));
              if (isDone(split.source)) {
                // A stalled or failing connection handed over everything: release it now.
                split.source.attempt?.abort();
                split.source.wake?.abort();
              }
            }
          }
          if (this.#active.size === 0) resolve();
        };
        this.#reschedule = schedule;
        schedule();
      });
    } finally {
      this.#reschedule = null;
      outerSignal.removeEventListener("abort", onAbort);
    }

    if (fatal) throw fatal;
    if (outerSignal.aborted) throw outerSignal.reason;
    if (!this.segments.every(isDone)) throw new Error("Stopped before every segment finished");
  }

  async #worker(seg, fh, signal) {
    let failures = 0;
    while (!isDone(seg)) {
      const before = seg.received;
      try {
        await this.#fetchRange(seg, fh, signal);
        if (seg.received === before && !isDone(seg)) throw fail("Server closed the connection without data", true);
      } catch (err) {
        if (signal.aborted) throw err;
        if (isDone(seg)) return; // another connection took over the rest of this segment
        if (seg.stalled) {
          seg.stalled = false; // recycled by the stall detector: reconnect right away
          continue;
        }
        const capped = err instanceof HttpError && (err.status === 429 || err.status === 503);
        if (capped && this.#active.size > 1) {
          // The server limits connections per client: run with fewer and hand this segment back.
          this.#limit = Math.max(1, this.#active.size - 1);
          this.#lastCappedAt = Date.now();
          return;
        }
        if (seg.received > before) failures = 0;
        if (!isRetryable(err) || ++failures > MAX_RETRIES) throw err;
        const wake = new AbortController(); // lets a takeover end the back-off early
        seg.wake = wake;
        try {
          await sleep(Math.min(30_000, 500 * 2 ** failures), AbortSignal.any([signal, wake.signal]));
        } catch (sleepErr) {
          if (signal.aborted) throw sleepErr;
        } finally {
          seg.wake = null;
        }
      }
    }
  }

  async #fetchRange(seg, fh, downloadSignal) {
    const attempt = new AbortController(); // aborted by the stall detector or a full takeover
    seg.attempt = attempt;
    seg.attemptStart = Date.now();
    const signal = AbortSignal.any([downloadSignal, attempt.signal]);
    const from = seg.start + seg.received;
    const headers = { ...this.headers, range: `bytes=${from}-${seg.end}` };
    if (this.validator) headers["if-range"] = this.validator;
    let res;
    try {
      res = await request(this.finalUrl ?? this.url, { headers, signal });
    } catch (err) {
      seg.attempt = null;
      throw err;
    }
    try {
      const status = res.statusCode ?? 0;
      if (status >= 400) throw new HttpError(status, res.finalUrl);
      if (status === 206) {
        const start = Number(/bytes\s+(\d+)-/i.exec(res.headers["content-range"] ?? "")?.[1]);
        if (start !== from) throw fail(`Server sent bytes from ${start}, expected ${from}`, false);
      } else if (from > 0) {
        throw fail("The server stopped honouring byte ranges or the file changed. Restart the download.", false);
      }

      for await (const chunk of res) {
        await this.limiter.take(chunk.length, signal);
        if (this.globalThrottle) await this.globalThrottle.take(chunk.length, signal);
        const pos = seg.start + seg.received;
        const allowed = seg.end - pos + 1; // `end` shrinks when another worker takes over the tail
        if (allowed <= 0) break;
        const piece = chunk.length > allowed ? chunk.subarray(0, allowed) : chunk;
        seg.inflight = piece.length;
        try {
          await fh.write(piece, 0, piece.length, pos);
        } finally {
          seg.inflight = 0;
        }
        seg.received += piece.length;
        seg.windowBytes = (seg.windowBytes ?? 0) + piece.length;
        seg.lastDataAt = Date.now();
        this.received += piece.length;
        this.#windowBytes += piece.length;
        if (isDone(seg)) break;
      }
    } finally {
      seg.attempt = null;
      if (!res.complete) res.destroy();
    }
  }

  /** Servers without range support: one connection, restarted from zero on failure. */
  async #runSingle(fh, signal) {
    let failures = 0;
    for (;;) {
      try {
        this.received = 0;
        await fh.truncate(0);
        const res = await request(this.url, { headers: this.headers, signal });
        try {
          if ((res.statusCode ?? 0) >= 400) throw new HttpError(res.statusCode, res.finalUrl);
          for await (const chunk of res) {
            await this.limiter.take(chunk.length, signal);
            if (this.globalThrottle) await this.globalThrottle.take(chunk.length, signal);
            await fh.write(chunk, 0, chunk.length, this.received);
            this.received += chunk.length;
            this.#windowBytes += chunk.length;
          }
        } finally {
          if (!res.complete) res.destroy();
        }
        if (this.size !== null && this.received < this.size) throw fail("Connection closed before the file finished", true);
        if (this.size === null) this.size = this.received;
        return;
      } catch (err) {
        if (signal.aborted || !isRetryable(err) || ++failures > MAX_RETRIES) throw err;
        await sleep(Math.min(30_000, 500 * 2 ** failures), signal);
      }
    }
  }

  async #loadSegments() {
    try {
      const saved = JSON.parse(await readFile(this.metaPath, "utf8"));
      const part = await stat(this.partPath);
      const sameFile =
        saved.size === this.size &&
        part.size === this.size &&
        (!saved.etag || !this.etag || saved.etag === this.etag) &&
        (!saved.lastModified || !this.lastModified || saved.lastModified === this.lastModified);
      if (!sameFile || !validSegments(saved.segments, this.size)) return null;
      return saved.segments.map(({ start, end, received }) => ({ start, end, received })).sort((a, b) => a.start - b.start);
    } catch {
      return null;
    }
  }

  #saveState(force = false) {
    if (!this.resumable || !this.fileName || !this.segments.length) return this.#saving;
    const now = Date.now();
    if (!force && now - this.#lastSave < SAVE_INTERVAL_MS) return this.#saving;
    this.#lastSave = now;
    const data = JSON.stringify({
      version: 1, url: this.url, size: this.size, etag: this.etag, lastModified: this.lastModified,
      segments: serializeSegments(this.segments),
    });
    const tmp = `${this.metaPath}.tmp`;
    this.#saving = this.#saving
      .then(async () => {
        await writeFile(tmp, data);
        await rename(tmp, this.metaPath);
      })
      .catch((err) => this.emit("warning", err));
    return this.#saving;
  }

  #tick() {
    const now = Date.now();
    const seconds = (now - this.#lastTick) / 1000;
    if (seconds <= 0) return;
    const flowing = this.#windowBytes > 0;
    const instant = this.#windowBytes / seconds;
    this.speed = this.speed ? this.speed * 0.7 + instant * 0.3 : instant;
    this.#windowBytes = 0;
    this.#lastTick = now;
    if (this.#reschedule) this.#watchConnections(now, seconds, flowing);
    if (this.status === "downloading") this.#saveState();
    this.emit("progress", this);
  }

  /** Segmented mode, once a second: measure each connection, recycle stalls, regain refused connections. */
  #watchConnections(now, seconds, flowing) {
    const measured = [];
    for (const seg of this.#active) {
      const instant = (seg.windowBytes ?? 0) / seconds;
      seg.windowBytes = 0;
      if (now - seg.activeSince >= 1500) {
        seg.speed = seg.speed === undefined ? instant : seg.speed * 0.5 + instant * 0.5;
        measured.push(seg.speed);
      }
      const quietFor = now - Math.max(seg.lastDataAt ?? 0, seg.attemptStart ?? 0);
      const othersCanHelp = flowing || this.#active.size < this.#limit;
      if (othersCanHelp && seg.attempt && quietFor > STALL_MS && !isDone(seg)) {
        seg.stalled = true; // data flows elsewhere (or slots are idle) but not here: reconnect it
        seg.attempt.abort();
      }
    }
    if (measured.length >= 2) {
      measured.sort((a, b) => a - b);
      const median = measured[Math.floor(measured.length / 2)];
      this.#connSpeed = this.#connSpeed ? this.#connSpeed * 0.8 + median * 0.2 : median;
    }
    if (this.#limit < this.connections && now - this.#lastCappedAt > RECOVER_MS) {
      this.#limit++; // the server refused connections earlier; probe whether it allows one more now
      this.#lastCappedAt = now;
    }
    this.#reschedule();
  }

  /** Expected speed of a fresh connection: the typical connection, not the slowest. */
  #helperSpeed() {
    return Math.max(this.#connSpeed || this.speed / Math.max(1, this.#active.size), MIN_HELPER_SPEED);
  }

  #setStatus(status) {
    if (this.status === status) return;
    this.status = status;
    this.emit("status", status, this);
  }
}

/** Reserve "name.ext", then "name (1).ext", … by creating the .part file exclusively. */
async function claimName(dir, name) {
  const dot = name.lastIndexOf(".");
  const stem = dot > 0 ? name.slice(0, dot) : name;
  const ext = dot > 0 ? name.slice(dot) : "";
  for (let i = 0; i < 1000; i++) {
    const candidate = i === 0 ? name : `${stem} (${i})${ext}`;
    if (await exists(join(dir, candidate))) continue;
    try {
      return { name: candidate, fh: await open(join(dir, `${candidate}.part`), "wx+") };
    } catch (err) {
      if (err.code !== "EEXIST") throw err;
    }
  }
  throw new Error(`No free file name for ${name}`);
}
