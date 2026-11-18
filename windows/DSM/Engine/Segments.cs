namespace Dsm.Engine;

/// <summary>An inclusive byte range [Start, End] of the file and how much of it is written.</summary>
public sealed class Segment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Received { get; set; }

    // Runtime state while a connection works on the segment (guarded by the download's lock).
    internal double? Speed;
    internal long WindowBytes;
    internal long Inflight;
    internal long ActiveSince;
    internal long LastDataAt;
    internal long AttemptStart;
    internal CancellationTokenSource? Attempt;
    internal CancellationTokenSource? Wake;
    internal bool Stalled;

    public long Length => End - Start + 1;
    public long Remaining => Math.Max(0, Length - Received);
    public bool IsDone => Remaining == 0;
}

/// <summary>Plain segment data for the resume file and the UI.</summary>
public sealed record SegmentState(long Start, long End, long Received);

public static class Segments
{
    public const long MinSegment = 1024 * 1024; // smallest initial slice worth its own connection
    public const long MinPiece = 32 * 1024;     // smallest tail worth handing to another connection

    public sealed record Split(Segment Piece, Segment Source);

    /// <summary>Divide <paramref name="size"/> bytes into at most <paramref name="connections"/> equal segments.</summary>
    public static List<Segment> Plan(long size, int connections, long minSegment = MinSegment)
    {
        var segments = new List<Segment>();
        if (size <= 0) return segments;
        var count = (int)Math.Max(1, Math.Min(connections, Math.Max(1, size / minSegment)));
        var size1 = size / count;
        long start = 0;
        for (var i = 0; i < count; i++)
        {
            var end = i == count - 1 ? size - 1 : start + size1 - 1;
            segments.Add(new Segment { Start = start, End = end });
            start = end + 1;
        }
        return segments;
    }

    /// <summary>
    /// Speed-aware dynamic segmentation for an idle connection: pick the segment expected
    /// to finish last and move its tail to a new connection, cut where both are expected to
    /// finish together (keep / slow = startup + (free - keep) / helper). A stalled
    /// connection keeps nothing; an average one keeps about half. Returns null when a new
    /// connection would not finish meaningfully sooner.
    /// </summary>
    public static Split? SplitForHelper(IEnumerable<Segment> candidates, double helperSpeed,
        double startupSeconds = 0.4, long minPiece = MinPiece)
    {
        var fast = Math.Max(helperSpeed, 1);
        Segment? source = null;
        double latest = 0;
        foreach (var s in candidates)
        {
            var free = s.Remaining - s.Inflight;
            if (free < 2 * minPiece) continue;
            var eta = free / Math.Max(s.Speed ?? fast, 1);
            if (eta > latest)
            {
                source = s;
                latest = eta;
            }
        }
        if (source is null) return null;

        var freeBytes = source.Remaining - source.Inflight;
        var slow = Math.Max(source.Speed ?? fast, 0);
        var keep = slow > 0 ? (long)Math.Round(slow * (startupSeconds * fast + freeBytes) / (slow + fast)) : 0;
        keep = Math.Clamp(keep, 0, freeBytes - minPiece);
        var alone = slow > 0 ? freeBytes / slow : double.PositiveInfinity;
        var together = startupSeconds + (freeBytes - keep) / fast;
        if (together >= alone * 0.8) return null;

        var cut = source.Start + source.Received + source.Inflight + keep;
        var piece = new Segment { Start = cut, End = source.End };
        source.End = cut - 1;
        return new Split(piece, source);
    }

    /// <summary>Insert keeping the list ordered by start offset.</summary>
    public static void Insert(List<Segment> segments, Segment piece)
    {
        var index = segments.FindIndex(s => s.Start > piece.Start);
        if (index < 0) segments.Add(piece);
        else segments.Insert(index, piece);
    }

    public static long ReceivedBytes(IEnumerable<Segment> segments) =>
        segments.Sum(s => Math.Min(s.Received, s.Length));

    /// <summary>Segments loaded from a resume file must tile [0, size) exactly.</summary>
    public static bool AreValid(IReadOnlyList<SegmentState>? segments, long size)
    {
        if (segments is null || segments.Count == 0) return false;
        long expected = 0;
        foreach (var s in segments.OrderBy(s => s.Start))
        {
            var length = s.End - s.Start + 1;
            if (s.Start != expected || s.End < s.Start || s.Received < 0 || s.Received > length) return false;
            expected = s.End + 1;
        }
        return expected == size;
    }
}
