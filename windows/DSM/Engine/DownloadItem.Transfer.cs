using System.Buffers;
using System.Runtime.ExceptionServices;
using Dsm.Platform;
using Microsoft.Win32.SafeHandles;

namespace Dsm.Engine;

public sealed partial class DownloadItem
{
    private async Task RunAsync(CancellationToken ct)
    {
        _lastTick = Environment.TickCount64;
        _windowBytes = 0;
        _fatal = null;
        LastTryAt = DateTime.Now;
        using var timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
        try
        {
            SetStatus(DownloadStatus.Connecting);
            var info = await Http.ProbeAsync(new Uri(Url), Headers, ct);
            FinalUrl = info.FinalUri;
            Size = info.Size;
            Resumable = info.Resumable && info.Size is not null;
            ETag = info.ETag;
            LastModified = info.LastModified;
            _validator = ETag is { } etag && !etag.StartsWith("W/", StringComparison.Ordinal) ? etag : LastModified;

            var name = FileName ?? SuggestedName ?? info.FileName;
            if (Folder.Length == 0)
            {
                Category = Categories.For(name);
                Folder = _folderFor?.Invoke(Category) ?? Native.DownloadsPath();
            }
            System.IO.Directory.CreateDirectory(Folder);

            List<Segment>? segments = null;
            if (FileName is null) FileName = ClaimName(Folder, name);
            else if (Resumable) segments = LoadSegments();
            if (segments is null) PreparePartFile(PartPath, Resumable ? Size : null);

            using (var file = File.OpenHandle(PartPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous))
            {
                if (Resumable)
                {
                    segments ??= Segments.Plan(Size!.Value, Connections);
                    lock (_sync)
                    {
                        _segments.Clear();
                        _segments.AddRange(segments);
                        Received = Segments.ReceivedBytes(_segments);
                    }
                    SetStatus(DownloadStatus.Downloading);
                    await SaveStateAsync(force: true);
                    await RunSegmentedAsync(file, ct);
                }
                else
                {
                    lock (_sync) _segments.Clear();
                    SetStatus(DownloadStatus.Downloading);
                    await RunSingleAsync(file, ct);
                }
            }

            await _saveLock.WaitAsync(CancellationToken.None); // let an in-flight resume save finish
            _saveLock.Release();
            MoveIntoPlace();
            CompletedAt = DateTime.Now;
            Speed = 0;
            SetStatus(DownloadStatus.Completed);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Error = Http.Describe(ex);
            await SaveStateAsync(force: true);
            Speed = 0;
            SetStatus(ct.IsCancellationRequested
                ? (_cancelRequested ? DownloadStatus.Cancelled : DownloadStatus.Paused)
                : DownloadStatus.Error);
        }
    }

    /// <summary>Keep up to <see cref="Connections"/> workers busy; hand tails to idle connections.</summary>
    private async Task RunSegmentedAsync(SafeFileHandle file, CancellationToken ct)
    {
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _limit = Connections;
            _connSpeed = 0;
        }

        void Activate(Segment seg) // caller holds _sync
        {
            seg.Speed = null;
            seg.WindowBytes = 0;
            seg.LastDataAt = 0;
            seg.ActiveSince = Environment.TickCount64;
            _active.Add(seg);
        }

        void Schedule()
        {
            var launch = new List<Segment>();
            var release = new List<CancellationTokenSource>();
            bool idle;
            lock (_sync)
            {
                if (!workers.IsCancellationRequested)
                {
                    foreach (var seg in _segments)
                    {
                        if (_active.Count >= _limit) break;
                        if (seg.IsDone || _active.Contains(seg)) continue;
                        Activate(seg);
                        launch.Add(seg);
                    }
                    while (_active.Count < _limit)
                    {
                        var split = Segments.SplitForHelper(_active, HelperSpeed());
                        if (split is null) break;
                        Segments.Insert(_segments, split.Piece);
                        Activate(split.Piece);
                        launch.Add(split.Piece);
                        if (split.Source.IsDone)
                        {
                            // A stalled or failing connection handed over everything: release it now.
                            if (split.Source.Attempt is { } attempt) release.Add(attempt);
                            if (split.Source.Wake is { } wake) release.Add(wake);
                        }
                    }
                }
                idle = _active.Count == 0;
            }
            foreach (var cts in release) TryCancel(cts);
            foreach (var seg in launch) _ = Task.Run(() => RunWorker(seg));
            if (idle) finished.TrySetResult();
        }

        async Task RunWorker(Segment seg)
        {
            try
            {
                await WorkerAsync(seg, file, workers.Token);
            }
            catch (Exception ex) when (!workers.IsCancellationRequested)
            {
                Interlocked.CompareExchange(ref _fatal, ex, null);
                TryCancel(workers);
            }
            catch
            {
                // Paused, cancelled, or another worker failed first.
            }
            finally
            {
                lock (_sync) _active.Remove(seg);
                Schedule();
            }
        }

        lock (_sync) _reschedule = Schedule;
        try
        {
            Schedule();
            await finished.Task;
        }
        finally
        {
            lock (_sync) _reschedule = null;
        }

        if (_fatal is { } fatal) ExceptionDispatchInfo.Throw(fatal);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_segments.All(s => s.IsDone)) throw new DownloadError("Stopped before every segment finished", retryable: true);
        }
    }

    private async Task WorkerAsync(Segment seg, SafeFileHandle file, CancellationToken ct)
    {
        var failures = 0;
        while (true)
        {
            long before;
            lock (_sync)
            {
                if (seg.IsDone) return;
                before = seg.Received;
            }
            try
            {
                await FetchRangeAsync(seg, file, ct);
                lock (_sync)
                {
                    if (seg.Received == before && !seg.IsDone)
                        throw new DownloadError("Server closed the connection without sending data", retryable: true);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                bool done, stalled;
                long received;
                lock (_sync)
                {
                    done = seg.IsDone;
                    stalled = seg.Stalled;
                    seg.Stalled = false;
                    received = seg.Received;
                }
                if (done) return;      // another connection took over the rest of this segment
                if (stalled) continue; // recycled by the stall detector: reconnect right away
                if (ex is HttpStatusError { Status: 429 or 503 } && ActiveConnections > 1)
                {
                    lock (_sync)
                    {
                        _limit = Math.Max(1, _active.Count - 1); // the server caps connections per client
                        _lastCappedAt = Environment.TickCount64;
                    }
                    return; // hand this segment back to the pool
                }
                if (received > before) failures = 0;
                if (!Http.IsRetryable(ex) || ++failures > MaxRetries) throw;

                using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct); // a takeover can end the wait early
                lock (_sync) seg.Wake = wake;
                try { await Task.Delay(Backoff(failures), wake.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* woken by a takeover */ }
                finally { lock (_sync) seg.Wake = null; }
            }
        }
    }

    private async Task FetchRangeAsync(Segment seg, SafeFileHandle file, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct); // stall detector / takeover cancel this
        long from, to;
        lock (_sync)
        {
            seg.Attempt = attempt;
            seg.AttemptStart = Environment.TickCount64;
            from = seg.Start + seg.Received;
            to = seg.End;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var (response, finalUri) = await Http.GetAsync(FinalUrl ?? new Uri(Url), Headers, from, to, _validator, attempt.Token);
            using (response)
            {
                var code = (int)response.StatusCode;
                if (code >= 400) throw new HttpStatusError(code, finalUri.Host);
                if (code == 206)
                {
                    var start = response.Content.Headers.ContentRange?.From;
                    if (start != from) throw new DownloadError($"Server sent bytes from {start}, expected {from}");
                }
                else if (from > 0)
                {
                    throw new DownloadError("The server stopped honouring byte ranges or the file changed. Restart the download.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token);
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, attempt.Token);
                    if (read == 0) break;
                    await _limiter.TakeAsync(read, attempt.Token);
                    if (_global is not null) await _global.TakeAsync(read, attempt.Token);

                    long position;
                    int count;
                    lock (_sync)
                    {
                        position = seg.Start + seg.Received;
                        var room = seg.End - position + 1; // End shrinks when another connection takes the tail
                        if (room <= 0) break;
                        count = (int)Math.Min(read, room);
                        seg.Inflight = count;
                    }
                    try { await RandomAccess.WriteAsync(file, buffer.AsMemory(0, count), position, CancellationToken.None); }
                    finally { lock (_sync) seg.Inflight = 0; }
                    lock (_sync)
                    {
                        seg.Received += count;
                        seg.WindowBytes += count;
                        seg.LastDataAt = Environment.TickCount64;
                        Received += count;
                        _windowBytes += count;
                        if (seg.IsDone) break;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            lock (_sync)
            {
                if (seg.Attempt == attempt) seg.Attempt = null;
            }
        }
    }

    /// <summary>Servers without range support: one connection, restarted from zero after a failure.</summary>
    private async Task RunSingleAsync(SafeFileHandle file, CancellationToken ct)
    {
        var seg = new Segment { Start = 0, End = long.MaxValue - 1 };
        lock (_sync)
        {
            _limit = 1;
            seg.ActiveSince = Environment.TickCount64;
            _active.Add(seg);
        }
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var failures = 0;
            while (true)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_sync)
                {
                    seg.Attempt = attempt;
                    seg.AttemptStart = Environment.TickCount64;
                    seg.Received = 0;
                    seg.Stalled = false;
                    Received = 0;
                }
                try
                {
                    RandomAccess.SetLength(file, 0);
                    var (response, finalUri) = await Http.GetAsync(new Uri(Url), Headers, null, null, null, attempt.Token);
                    using (response)
                    {
                        if ((int)response.StatusCode >= 400) throw new HttpStatusError((int)response.StatusCode, finalUri.Host);
                        await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token);
                        while (true)
                        {
                            var read = await stream.ReadAsync(buffer, attempt.Token);
                            if (read == 0) break;
                            await _limiter.TakeAsync(read, attempt.Token);
                            if (_global is not null) await _global.TakeAsync(read, attempt.Token);
                            await RandomAccess.WriteAsync(file, buffer.AsMemory(0, read), seg.Received, CancellationToken.None);
                            lock (_sync)
                            {
                                seg.Received += read;
                                seg.WindowBytes += read;
                                seg.LastDataAt = Environment.TickCount64;
                                Received += read;
                                _windowBytes += read;
                            }
                        }
                    }
                    if (Size is long size && Received < size)
                        throw new DownloadError("Connection closed before the file finished", retryable: true);
                    Size ??= Received;
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (!Http.IsRetryable(ex) || ++failures > MaxRetries) throw;
                    await Task.Delay(Backoff(failures), ct);
                }
                finally
                {
                    lock (_sync)
                    {
                        if (seg.Attempt == attempt) seg.Attempt = null;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            lock (_sync) _active.Remove(seg);
        }
    }

    private static TimeSpan Backoff(int failures) => TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * Math.Pow(2, failures)));

    /// <summary>Create (or truncate) the .part file; pre-size it as a sparse file when the size is known.</summary>
    private static void PreparePartFile(string path, long? size)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        if (size is > 0)
        {
            Native.TryMarkSparse(stream.SafeFileHandle);
            stream.SetLength(size.Value);
        }
    }

    /// <summary>Reserve "name.ext", then "name (1).ext", … by creating the .part file exclusively.</summary>
    private static string ClaimName(string folder, string name)
    {
        foreach (var candidate in Candidates(name))
        {
            var target = Path.Combine(folder, candidate);
            if (File.Exists(target)) continue;
            try
            {
                using (new FileStream(target + ".part", FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                return candidate;
            }
            catch (IOException) when (File.Exists(target + ".part"))
            {
                // Someone else holds this name; try the next one.
            }
        }
        throw new DownloadError($"No free file name for {name}");
    }

    private static IEnumerable<string> Candidates(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        yield return name;
        for (var i = 1; i < 1000; i++) yield return $"{stem} ({i}){ext}";
    }

    private void MoveIntoPlace()
    {
        var part = PartPath;
        var meta = MetaPath;
        if (File.Exists(TargetPath))
            FileName = Candidates(FileName!).First(c => !File.Exists(Path.Combine(Folder, c)));
        File.Move(part, TargetPath!);
        TryDelete(meta);
    }
}
