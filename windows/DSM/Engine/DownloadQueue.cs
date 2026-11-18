using Dsm.Platform;

namespace Dsm.Engine;

public sealed record AddRequest(string Url, string? FileName = null, string? Folder = null, int? Connections = null,
    IReadOnlyDictionary<string, string>? Headers = null, bool StartNow = true);

/// <summary>All downloads: concurrency limit, global speed limit and persistence across restarts.</summary>
public sealed class DownloadQueue
{
    private static readonly HashSet<string> ForwardedHeaders =
        new(StringComparer.OrdinalIgnoreCase) { "cookie", "referer", "user-agent", "authorization" };

    private readonly object _gate = new();
    private readonly object _flushGate = new();
    private readonly List<DownloadItem> _items = [];
    private readonly Throttle _global = new();
    private readonly System.Threading.Timer _saveTimer;
    private bool _pumping;
    private bool _pumpAgain;

    public DownloadQueue(AppSettings settings)
    {
        Settings = settings;
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        ApplySettings();
    }

    public AppSettings Settings { get; }

    public event Action<DownloadItem>? ItemAdded;
    public event Action<DownloadItem>? ItemRemoved;
    public event Action<DownloadItem>? ItemChanged;
    public event Action<DownloadItem>? ItemProgress;
    public event Action<DownloadItem>? ItemCompleted;

    private static string ListPath => Path.Combine(AppPaths.DataDir, "downloads.json");

    public IReadOnlyList<DownloadItem> Items
    {
        get { lock (_gate) return [.. _items]; }
    }

    public DownloadItem? Find(string id)
    {
        lock (_gate) return _items.FirstOrDefault(i => i.Id == id);
    }

    public void Load()
    {
        foreach (var record in Json.Read<List<DownloadRecord>>(ListPath) ?? [])
        {
            // Interrupted downloads come back paused; their .part.dsm file knows where to resume.
            if (record.Status is not (DownloadStatus.Completed or DownloadStatus.Error or DownloadStatus.Cancelled))
                record.Status = DownloadStatus.Paused;
            Track(new DownloadItem(record, _global, Settings.FolderFor));
        }
    }

    public DownloadItem Add(AddRequest request)
    {
        var uri = new Uri(request.Url);
        if (uri.Scheme is not ("http" or "https")) throw new ArgumentException("Only http and https links are supported.");
        var headers = (request.Headers ?? new Dictionary<string, string>())
            .Where(h => ForwardedHeaders.Contains(h.Key) && !string.IsNullOrEmpty(h.Value) && h.Value.IndexOfAny(['\r', '\n']) < 0)
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value);
        var record = new DownloadRecord
        {
            Url = uri.AbsoluteUri,
            SuggestedName = request.FileName,
            Folder = request.Folder ?? "", // empty: decided by category once the file name is known
            Category = Categories.For(request.FileName ?? Http.FileNameFromUrl(uri)),
            Connections = request.Connections ?? Settings.DefaultConnections,
            Headers = headers,
            Status = request.StartNow ? DownloadStatus.Queued : DownloadStatus.Paused,
            CreatedAt = DateTime.Now,
        };
        var item = new DownloadItem(record, _global, Settings.FolderFor);
        Track(item);
        ItemAdded?.Invoke(item);
        ScheduleSave();
        Pump();
        return item;
    }

    public void Resume(DownloadItem item)
    {
        item.Queue();
        Pump();
    }

    public async Task RemoveAsync(DownloadItem item, bool deleteFile)
    {
        if (item.Status != DownloadStatus.Completed) await item.CancelAsync();
        else if (deleteFile && item.TargetPath is { } path) TryDelete(path);
        lock (_gate) _items.Remove(item);
        ItemRemoved?.Invoke(item);
        ScheduleSave();
    }

    public Task PauseAllAsync() =>
        Task.WhenAll(Items.Where(i => i.IsRunning || i.Status == DownloadStatus.Queued).Select(i => i.PauseAsync()));

    public void ResumeAll()
    {
        foreach (var item in Items.Where(i => i.Status is DownloadStatus.Paused or DownloadStatus.Error)) item.Queue();
        Pump();
    }

    public void ApplySettings()
    {
        _global.Rate = Settings.GlobalSpeedLimit;
        Http.PreferIPv4 = Settings.PreferIPv4;
        Pump();
    }

    public async Task ShutdownAsync()
    {
        await Task.WhenAll(Items.Where(i => i.IsRunning).Select(i => i.PauseAsync()));
        Flush();
    }

    public void Flush()
    {
        try
        {
            lock (_flushGate) Json.WriteAtomic(ListPath, Items.Select(i => i.ToRecord()).ToList());
        }
        catch
        {
            // Retried on the next change.
        }
    }

    private void ScheduleSave() => _saveTimer.Change(500, Timeout.Infinite);

    private void Track(DownloadItem item)
    {
        lock (_gate) _items.Add(item);
        item.StatusChanged += OnStatusChanged;
        item.Progress += i => ItemProgress?.Invoke(i);
    }

    private void OnStatusChanged(DownloadItem item)
    {
        ScheduleSave();
        Pump();
        ItemChanged?.Invoke(item);
        if (item.Status == DownloadStatus.Completed) ItemCompleted?.Invoke(item);
    }

    /// <summary>Start queued downloads, oldest first, while slots are free. Safe to call from any thread.</summary>
    private void Pump()
    {
        lock (_gate)
        {
            if (_pumping)
            {
                _pumpAgain = true;
                return;
            }
            _pumping = true;
            _pumpAgain = false;
        }
        while (true)
        {
            var items = Items;
            // Count transferring downloads plus ones just started but not yet connecting.
            var running = items.Count(i => i.IsActive || (i.Status == DownloadStatus.Queued && i.IsRunning));
            foreach (var item in items)
            {
                if (running >= Settings.MaxConcurrent) break;
                if (item.Status != DownloadStatus.Queued || item.IsRunning) continue;
                running++;
                _ = item.StartAsync();
            }
            lock (_gate)
            {
                if (!_pumpAgain)
                {
                    _pumping = false;
                    return;
                }
                _pumpAgain = false;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
