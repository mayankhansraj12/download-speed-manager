using System.Text.Json;
using Dsm.Platform;

namespace Dsm.Engine;

public enum DownloadStatus { Queued, Connecting, Downloading, Paused, Completed, Error, Cancelled }

/// <summary>What the download list stores for each download.</summary>
public sealed class DownloadRecord
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string? FileName { get; set; }
    public string? SuggestedName { get; set; }
    public string Folder { get; set; } = "";
    public string Category { get; set; } = "Other";
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long? Size { get; set; }
    public long Received { get; set; }
    public int Connections { get; set; } = 8;
    public long SpeedLimit { get; set; }
    public bool Resumable { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastTryAt { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
}

public readonly record struct ConnectionInfo(int Number, long Start, long End, long Received, double Speed, string State);

internal sealed class ResumeState
{
    public int Version { get; set; } = 1;
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public List<SegmentState> Segments { get; set; } = [];
}

public sealed partial class DownloadItem
{
    private const int MaxRetries = 8;
    private const long SaveIntervalMs = 2000;
    private const long StallMs = 6000;     // reconnect a quiet connection while others are flowing
    private const long DeadMs = 30_000;    // reconnect any connection that has been silent this long
    private const long RecoverMs = 10_000; // after a server refused connections, re-test one this often
    private const double MinHelperSpeed = 32 * 1024;

    private readonly object _sync = new();
    private readonly Throttle _limiter = new();
    private readonly Throttle? _global;
    private readonly Func<string, string>? _folderFor;
    private readonly List<Segment> _segments = [];
    private readonly HashSet<Segment> _active = [];
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _running;
    private volatile bool _cancelRequested;
    private Action? _reschedule;
    private Exception? _fatal;
    private int _limit = 1;
    private double _connSpeed;
    private long _lastCappedAt;
    private long _windowBytes;
    private long _lastTick;
    private long _lastSave;
    private string? _validator;

    public DownloadItem(DownloadRecord record, Throttle? globalThrottle = null, Func<string, string>? folderFor = null)
    {
        Id = string.IsNullOrEmpty(record.Id) ? Guid.NewGuid().ToString("N") : record.Id;
        Url = record.Url;
        FileName = record.FileName;
        SuggestedName = string.IsNullOrWhiteSpace(record.SuggestedName) ? null : Http.Sanitize(record.SuggestedName);
        Folder = record.Folder;
        Category = record.Category;
        Status = record.Status;
        Size = record.Size;
        Received = record.Received;
        Connections = Math.Clamp(record.Connections, 1, 32);
        _limiter.Rate = record.SpeedLimit;
        Resumable = record.Resumable;
        Error = record.Error;
        CreatedAt = record.CreatedAt == default ? DateTime.Now : record.CreatedAt;
        CompletedAt = record.CompletedAt;
        LastTryAt = record.LastTryAt;
        Headers = new Dictionary<string, string>(record.Headers, StringComparer.OrdinalIgnoreCase);
        _global = globalThrottle;
        _folderFor = folderFor;
    }

    public string Id { get; }
    public string Url { get; }
    public string? FileName { get; private set; }
    public string? SuggestedName { get; }
    public string Folder { get; private set; }
    public string Category { get; private set; }
    public DownloadStatus Status { get; private set; }
    public long? Size { get; private set; }
    public long Received { get; private set; }
    public int Connections { get; private set; }
    public long SpeedLimit => (long)_limiter.Rate;
    public bool Resumable { get; private set; }
    public string? Error { get; private set; }
    public DateTime CreatedAt { get; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? LastTryAt { get; private set; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public Uri? FinalUrl { get; private set; }
    public string? ETag { get; private set; }
    public string? LastModified { get; private set; }
    public double Speed { get; private set; }

    public string DisplayName => FileName ?? SuggestedName ?? Http.FileNameFromUrl(new Uri(Url));
    public string? TargetPath => FileName is null || Folder.Length == 0 ? null : Path.Combine(Folder, FileName);
    private string PartPath => TargetPath + ".part";
    private string MetaPath => TargetPath + ".part.dsm";
    public bool IsRunning { get { lock (_sync) return _running is { IsCompleted: false }; } }
    public bool IsActive => Status is DownloadStatus.Connecting or DownloadStatus.Downloading;
    public int ActiveConnections { get { lock (_sync) return _active.Count; } }
    public double? EtaSeconds => Size is long size && Speed > 1 ? Math.Max(0, (size - Received) / Speed) : null;
    public double? Fraction => Status == DownloadStatus.Completed ? 1 : Size is long size && size > 0 ? Math.Min(1, (double)Received / size) : null;

    /// <summary>Raised on a background thread whenever <see cref="Status"/> changes.</summary>
    public event Action<DownloadItem>? StatusChanged;

    /// <summary>Raised on a background thread about once a second while running.</summary>
    public event Action<DownloadItem>? Progress;

    public Task StartAsync()
    {
        lock (_sync)
        {
            if (_running is { IsCompleted: false }) return _running;
            if (Status == DownloadStatus.Completed) return Task.CompletedTask;
            _cancelRequested = false;
            Error = null;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _running = Task.Run(() => RunAsync(token));
            return _running;
        }
    }

    /// <summary>Wait for a free slot; the queue starts queued downloads in order.</summary>
    public void Queue()
    {
        if (!IsRunning && Status != DownloadStatus.Completed) SetStatus(DownloadStatus.Queued);
    }

    public async Task PauseAsync()
    {
        Task? running;
        lock (_sync) running = _running is { IsCompleted: false } ? _running : null;
        if (running is not null)
        {
            _cts?.Cancel();
            await running;
        }
        else if (Status is DownloadStatus.Queued or DownloadStatus.Error)
        {
            SetStatus(DownloadStatus.Paused);
        }
    }

    /// <summary>Stop and delete the partial file. Completed files are left alone.</summary>
    public async Task CancelAsync()
    {
        if (Status == DownloadStatus.Completed) return;
        _cancelRequested = true;
        Task? running;
        lock (_sync) running = _running is { IsCompleted: false } ? _running : null;
        if (running is not null)
        {
            _cts?.Cancel();
            await running;
        }
        if (TargetPath is not null)
        {
            TryDelete(PartPath);
            TryDelete(MetaPath);
        }
        SetStatus(DownloadStatus.Cancelled);
    }

    public void SetConnections(int count)
    {
        Action? reschedule;
        lock (_sync)
        {
            Connections = Math.Clamp(count, 1, 32);
            _limit = Connections;
            reschedule = _reschedule;
        }
        reschedule?.Invoke();
    }

    public void SetSpeedLimit(long bytesPerSecond) => _limiter.Rate = Math.Max(0, bytesPerSecond);

    public SegmentState[] SnapshotSegments()
    {
        lock (_sync) return [.. _segments.Select(s => new SegmentState(s.Start, s.End, s.Received))];
    }

    public ConnectionInfo[] SnapshotConnections()
    {
        lock (_sync)
        {
            var number = 0;
            return [.. _active.OrderBy(s => s.Start).Select(s => new ConnectionInfo(++number, s.Start, s.End, s.Received,
                s.Speed ?? 0, s.Attempt is null ? "Waiting to retry" : s.LastDataAt < s.AttemptStart ? "Connecting" : "Receiving data"))];
        }
    }

    public DownloadRecord ToRecord() => new()
    {
        Id = Id, Url = Url, FileName = FileName, SuggestedName = SuggestedName, Folder = Folder, Category = Category,
        Status = Status, Size = Size, Received = Received, Connections = Connections, SpeedLimit = SpeedLimit,
        Resumable = Resumable, Error = Error, CreatedAt = CreatedAt, CompletedAt = CompletedAt, LastTryAt = LastTryAt,
        Headers = new Dictionary<string, string>(Headers),
    };

    private void SetStatus(DownloadStatus status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke(this);
    }

    /// <summary>Once a second: speeds, stall recycling, connection-limit recovery, progress events.</summary>
    private void Tick()
    {
        try
        {
            var now = Environment.TickCount64;
            var seconds = (now - _lastTick) / 1000.0;
            if (seconds <= 0) return;
            var reschedule = _reschedule;
            var stalled = new List<CancellationTokenSource>();
            lock (_sync)
            {
                var flowing = _windowBytes > 0;
                var instant = _windowBytes / seconds;
                Speed = Speed > 0 ? Speed * 0.7 + instant * 0.3 : instant;
                _windowBytes = 0;
                _lastTick = now;

                var measured = new List<double>();
                foreach (var seg in _active)
                {
                    var segInstant = seg.WindowBytes / seconds;
                    seg.WindowBytes = 0;
                    if (now - seg.ActiveSince >= 1500)
                    {
                        seg.Speed = seg.Speed is double previous ? previous * 0.5 + segInstant * 0.5 : segInstant;
                        measured.Add(seg.Speed.Value);
                    }
                    var quiet = now - Math.Max(seg.LastDataAt, seg.AttemptStart);
                    var othersCanHelp = flowing || _active.Count < _limit;
                    if (seg.Attempt is { } attempt && !seg.IsDone && ((othersCanHelp && quiet > StallMs) || quiet > DeadMs))
                    {
                        seg.Stalled = true;
                        stalled.Add(attempt);
                    }
                }
                if (measured.Count >= 2)
                {
                    measured.Sort();
                    var median = measured[measured.Count / 2];
                    _connSpeed = _connSpeed > 0 ? _connSpeed * 0.8 + median * 0.2 : median;
                }
                if (reschedule is not null && _limit < Connections && now - _lastCappedAt > RecoverMs)
                {
                    _limit++; // probe whether the server accepts one more connection now
                    _lastCappedAt = now;
                }
            }
            foreach (var attempt in stalled) TryCancel(attempt);
            reschedule?.Invoke();
            if (Status == DownloadStatus.Downloading) _ = SaveStateAsync();
            Progress?.Invoke(this);
        }
        catch
        {
            // A timer callback must never take the application down.
        }
    }

    private double HelperSpeed() =>
        Math.Max(_connSpeed > 0 ? _connSpeed : Speed / Math.Max(1, _active.Count), MinHelperSpeed);

    private async Task SaveStateAsync(bool force = false)
    {
        if (!Resumable || TargetPath is null) return;
        ResumeState state;
        lock (_sync)
        {
            var now = Environment.TickCount64;
            if (_segments.Count == 0 || (!force && now - _lastSave < SaveIntervalMs)) return;
            _lastSave = now;
            state = new ResumeState
            {
                Url = Url, Size = Size ?? 0, ETag = ETag, LastModified = LastModified,
                Segments = [.. _segments.Select(s => new SegmentState(s.Start, s.End, s.Received))],
            };
        }
        await _saveLock.WaitAsync();
        try
        {
            var tmp = MetaPath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(state, Json.Options));
            File.Move(tmp, MetaPath, overwrite: true);
        }
        catch
        {
            // Retried on the next save.
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private List<Segment>? LoadSegments()
    {
        try
        {
            var saved = Json.Read<ResumeState>(MetaPath);
            var part = new FileInfo(PartPath);
            if (saved is null || Size is not long size || !part.Exists) return null;
            var sameFile = saved.Size == size && part.Length == size
                && (saved.ETag is null || ETag is null || saved.ETag == ETag)
                && (saved.LastModified is null || LastModified is null || saved.LastModified == LastModified);
            if (!sameFile || !Segments.AreValid(saved.Segments, size)) return null;
            return [.. saved.Segments.OrderBy(s => s.Start).Select(s => new Segment { Start = s.Start, End = s.End, Received = s.Received })];
        }
        catch
        {
            return null;
        }
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* attempt already finished */ }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
