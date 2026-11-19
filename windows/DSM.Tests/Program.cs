using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Dsm;
using Dsm.Engine;
using Dsm.Integration;
using Dsm.Platform;
using Dsm.Tests;
using Dsm.Updates;

const int MB = 1024 * 1024;
const int KB = 1024;

var tests = new (string Name, Func<Task> Run)[]
{
    ("multi-connection download is byte-identical and cleans up", MultiConnection),
    ("falls back to one connection without range support", NoRanges),
    ("follows redirects and never overwrites existing files", RedirectsAndUniqueNames),
    ("fast connections take over a slow segment", SlowSegmentTakeover),
    ("a connection that goes silent is recycled", StalledConnection),
    ("pause and resume continue from the saved segments", PauseAndResume),
    ("speed limit holds the transfer near the configured rate", SpeedLimit),
    ("adapts when the server caps connections (HTTP 429)", ConnectionCap),
    ("queue respects the simultaneous-download limit", QueueConcurrency),
    ("segment takeover math", SegmentMath),
    ("file names are safe on Windows", FileNames),
    ("clipboard: links restored by dictation tools aren't offered again", ClipboardRestores),
    ("updates: version numbers and the built-in signing key", UpdateBasics),
    ("updates: a signed newer release is offered and its installer verified", UpdateAvailable),
    ("updates: unsigned, altered or older releases are refused", UpdateRefused),
    ("updates: an installer that doesn't match the checksum is deleted", UpdateBadInstaller),
    ("command line: --update asks the app to update", UpdateCommand),
};

var only = args.Length > 0 ? args[0] : null;
var failed = 0;
foreach (var (name, run) in tests.Where(t => only is null || t.Name.Contains(only, StringComparison.OrdinalIgnoreCase)))
{
    var watch = Stopwatch.StartNew();
    try
    {
        await run();
        Console.WriteLine($"PASS  {name} ({watch.ElapsedMilliseconds} ms)");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}: {ex.Message}");
    }
}
Console.WriteLine(failed == 0 ? "All tests passed." : $"{failed} test(s) failed.");
return failed == 0 ? 0 : 1;

static async Task MultiConnection()
{
    var body = RandomNumberGenerator.GetBytes(8 * MB);
    using var server = new RangeServer(body);
    await WithTemp(async dir =>
    {
        var d = New(server.BaseUrl + "/file", dir, 8);
        await d.StartAsync();
        Check(d.Status == DownloadStatus.Completed, d.Error);
        Check(d.FileName == "test file.bin", $"name was {d.FileName}");
        Check(Same(body, d.TargetPath!), "content differs");
        Check(server.MaxServing > 1, "expected parallel connections");
        Check(Names(dir).SequenceEqual(["test file.bin"]), "leftover files: " + string.Join(", ", Names(dir)));
    });
}

static async Task NoRanges()
{
    var body = RandomNumberGenerator.GetBytes(3 * MB);
    using var server = new RangeServer(body);
    await WithTemp(async dir =>
    {
        var d = new DownloadItem(new DownloadRecord { Url = server.BaseUrl + "/norange", Folder = dir, Connections = 8, SuggestedName = "plain.bin" });
        await d.StartAsync();
        Check(d.Status == DownloadStatus.Completed, d.Error);
        Check(!d.Resumable, "should not be resumable");
        Check(Same(body, Path.Combine(dir, "plain.bin")), "content differs");
    });
}

static async Task RedirectsAndUniqueNames()
{
    var body = RandomNumberGenerator.GetBytes(2 * MB);
    using var server = new RangeServer(body);
    await WithTemp(async dir =>
    {
        var first = New(server.BaseUrl + "/redirect", dir, 8);
        var second = New(server.BaseUrl + "/redirect", dir, 8);
        await Task.WhenAll(first.StartAsync(), second.StartAsync());
        Check(first.Status == DownloadStatus.Completed && second.Status == DownloadStatus.Completed, first.Error ?? second.Error);
        Check(Names(dir).SequenceEqual(["test file (1).bin", "test file.bin"]), "names: " + string.Join(", ", Names(dir)));
        Check(Same(body, second.TargetPath!), "content differs");
    });
}

static async Task SlowSegmentTakeover()
{
    var body = RandomNumberGenerator.GetBytes(4 * MB);
    using var server = new RangeServer(body) { DelayFor = start => start == 0 ? 40 : 0 }; // first half is slow
    await WithTemp(async dir =>
    {
        var d = New(server.BaseUrl + "/file", dir, 2);
        var watch = Stopwatch.StartNew();
        await d.StartAsync();
        Check(d.Status == DownloadStatus.Completed, d.Error);
        Check(d.SnapshotSegments().Length > 2, $"expected takeovers, got {d.SnapshotSegments().Length} segments");
        Check(watch.ElapsedMilliseconds < 32 * 40, $"took {watch.ElapsedMilliseconds} ms; should beat the slow connection alone");
        Check(Same(body, d.TargetPath!), "content differs");
    });
}

static async Task StalledConnection()
{
    var body = RandomNumberGenerator.GetBytes(4 * MB);
    using var server = new RangeServer(body) { StallFor = start => start == 0 }; // first segment dies after 64 KiB
    await WithTemp(async dir =>
    {
        var d = New(server.BaseUrl + "/file", dir, 2);
        var watch = Stopwatch.StartNew();
        await d.StartAsync();
        Check(d.Status == DownloadStatus.Completed, d.Error);
        Check(watch.Elapsed.TotalSeconds < 12, $"took {watch.Elapsed.TotalSeconds:F1}s; the 30 s dead-connection limit should not be needed");
        Check(Same(body, d.TargetPath!), "content differs");
    });
}

static async Task PauseAndResume()
{
    var body = RandomNumberGenerator.GetBytes(8 * MB);
    using var server = new RangeServer(body) { DelayFor = _ => 25 };
    await WithTemp(async dir =>
    {
        var first = New(server.BaseUrl + "/file", dir, 4);
        var run = first.StartAsync();
        await WaitUntil(() => first.Received >= 2 * MB, "first 2 MB");
        await first.PauseAsync();
        await run;
        Check(first.Status == DownloadStatus.Paused, $"status was {first.Status}");
        Check(first.Received < body.Length, "finished before the pause");
        Check(File.Exists(Path.Combine(dir, "test file.bin.part.dsm")), "resume file missing");

        var servedBefore = server.BytesServed;
        var resumed = New(server.BaseUrl + "/file", dir, 4, fileName: first.FileName);
        await resumed.StartAsync();
        Check(resumed.Status == DownloadStatus.Completed, resumed.Error);
        Check(server.BytesServed - servedBefore < body.Length, "resume re-downloaded everything");
        Check(Same(body, resumed.TargetPath!), "content differs");
        Check(Names(dir).SequenceEqual(["test file.bin"]), "leftover files: " + string.Join(", ", Names(dir)));
    });
}

static async Task SpeedLimit()
{
    var body = RandomNumberGenerator.GetBytes(1 * MB);
    using var server = new RangeServer(body);
    await WithTemp(async dir =>
    {
        var d = New(server.BaseUrl + "/file", dir, 4, speedLimit: 512 * KB);
        var watch = Stopwatch.StartNew();
        await d.StartAsync();
        var seconds = watch.Elapsed.TotalSeconds;
        Check(d.Status == DownloadStatus.Completed, d.Error);
        Check(seconds >= 1.4 && seconds < 6, $"took {seconds:F2}s at 512 KiB/s");
    });
}

static async Task ConnectionCap()
{
    var body = RandomNumberGenerator.GetBytes(4 * MB);
    using var server = new RangeServer(body) { MaxConnections = 3, DelayFor = _ => 5 };
    await WithTemp(async dir =>
    {
        var d = New(server.BaseUrl + "/file", dir, 8);
        await d.StartAsync();
        Check(d.Status == DownloadStatus.Completed, d.Error);
        Check(server.MaxServing <= 3, $"served {server.MaxServing} at once");
        Check(Same(body, d.TargetPath!), "content differs");
    });
}

static async Task QueueConcurrency()
{
    var body = RandomNumberGenerator.GetBytes(2 * MB);
    using var server = new RangeServer(body) { DelayFor = _ => 5 };
    await WithTemp(async dir =>
    {
        AppPaths.DataDir = Path.Combine(dir, "data");
        var settings = new AppSettings { DownloadFolder = dir, SortByCategory = false, MaxConcurrent = 2, DefaultConnections = 2 };
        var queue = new DownloadQueue(settings);
        var maxActive = 0;
        queue.ItemChanged += _ =>
        {
            var active = queue.Items.Count(i => i.IsActive);
            lock (queue) maxActive = Math.Max(maxActive, active);
        };
        for (var i = 0; i < 4; i++) queue.Add(new AddRequest(server.BaseUrl + "/file", FileName: $"f{i}.bin"));
        await WaitUntil(() => queue.Items.All(i => i.Status == DownloadStatus.Completed), "all queued downloads", seconds: 30);
        Check(maxActive <= 2, $"{maxActive} downloads ran at once");
        foreach (var i in Enumerable.Range(0, 4)) Check(Same(body, Path.Combine(dir, $"f{i}.bin")), $"f{i}.bin differs");
        queue.Flush();
        Check(File.Exists(Path.Combine(dir, "data", "downloads.json")), "download list was not saved");
    });
}

static Task SegmentMath()
{
    var even = new Segment { Start = 0, End = 4 * MB - 1, Received = MB, Speed = 300 * KB };
    var half = Segments.SplitForHelper([even], 300 * KB)!;
    Check(even.End + 1 == half.Piece.Start && half.Piece.End == 4 * MB - 1, "pieces must tile the range");
    Check(Math.Abs(half.Piece.Start - 2.5 * MB) < 100 * KB, $"average split at {half.Piece.Start}");

    var slow = new Segment { Start = 0, End = MB - 1, Received = MB / 2, Speed = 20 * KB };
    var tail = Segments.SplitForHelper([slow], 400 * KB)!;
    Check(tail.Piece.Length > 0.9 * (MB / 2), "a fast helper should take most of a slow tail");

    var stalled = new Segment { Start = 0, End = MB - 1, Received = 1000, Speed = 0 };
    var all = Segments.SplitForHelper([stalled], 400 * KB)!;
    Check(all.Piece.Start == 1000 && stalled.IsDone, "a stalled segment hands over everything");

    var nearlyDone = new Segment { Start = 0, End = 200 * KB, Received = 10 * KB, Speed = 400 * KB };
    Check(Segments.SplitForHelper([nearlyDone], 400 * KB) is null, "not worth a new connection");

    Check(Segments.Plan(10 * MB, 8).Count == 8 && Segments.Plan(500 * KB, 8).Count == 1 && Segments.Plan(0, 8).Count == 0, "planning");
    return Task.CompletedTask;
}

static Task FileNames()
{
    Check(Http.Sanitize("a<b>:c\"d.txt") == "a_b__c_d.txt", Http.Sanitize("a<b>:c\"d.txt"));
    Check(Http.Sanitize("con.txt") == "_con.txt", "reserved names");
    Check(Http.Sanitize("  ") == "download", "empty names");
    Check(Http.FileNameFromUrl(new Uri("http://x/a/setup%20v2.exe?token=1")) == "setup v2.exe", "URL names");
    Check(Categories.For("movie.mkv") == "Video" && Categories.For("x.unknown") == "Other", "categories");
    return Task.CompletedTask;
}

static Task ClipboardRestores()
{
    const string link = "https://example.com/files/movie.mkv";

    // Wispr Flow: you copied a link earlier; dictating puts text on the clipboard, pastes it, then restores the link.
    var filter = new ClipboardLinkFilter();
    Check(filter.Offer(link) == link, "a newly copied link is offered");
    Check(filter.Offer("Let's meet at five tomorrow") is null, "dictated text is not a link");
    Check(filter.Offer(link) is null, "the restored link must not be offered again");
    Check(filter.Offer("https://example.com/about") is null, "ordinary pages are not offered");

    var startup = new ClipboardLinkFilter();
    startup.MarkSeen(link);
    Check(startup.Offer(link) is null, "a link already on the clipboard at startup is never offered");

    var listed = new ClipboardLinkFilter { AlreadyInList = url => url == link };
    Check(listed.Offer(link) is null, "a link already in the download list is not offered");

    var privateContent = new ClipboardLinkFilter();
    Check(privateContent.Offer(link, excludedByOwner: true) is null, "content marked private is not offered");
    Check(privateContent.Offer(link) is null, "...nor when it is restored later");

    var disabled = new ClipboardLinkFilter();
    Check(disabled.Offer(link, enabled: false) is null && disabled.Offer(link) is null, "links seen while catching was off stay quiet");

    Check(new ClipboardLinkFilter().Offer("  https://example.com/a.zip \r\n") == "https://example.com/a.zip", "surrounding whitespace is ignored");
    return Task.CompletedTask;
}

static Task UpdateBasics()
{
    Check(UpdateManifest.TryParseVersion("v1.2.3", out var tagged) && tagged == new Version(1, 2, 3), "v-prefixed tags");
    Check(UpdateManifest.TryParseVersion("10.0.1", out var ten) && ten > new Version(9, 9, 9), "numeric, not alphabetical, order");
    Check(!UpdateManifest.TryParseVersion("1.2", out _), "needs three parts");
    Check(!UpdateManifest.TryParseVersion("1.2.3-beta", out _), "pre-releases are not offered as updates");
    Check(!UpdateManifest.TryParseVersion("99999999999.0.0", out _), "overflow is rejected, not thrown");
    var keys = UpdateService.PublicKeys().ToList();
    Check(keys.Count > 0, "the app must embed at least one update key");
    foreach (var publicKey in keys)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        Check(key.KeySize == 256, "update keys are P-256");
    }
    return Task.CompletedTask;
}

static async Task UpdateAvailable()
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string[] trusted = [Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())];
    var setup = RandomNumberGenerator.GetBytes(300 * KB);
    using var server = new FeedServer();
    server.Files["/download/v1.4.0/DownloadSpeedManager-Setup-1.4.0.exe"] = setup;
    var manifest = FeedServer.Manifest("1.4.0", server.BaseUrl + "/download/v1.4.0/DownloadSpeedManager-Setup-1.4.0.exe", setup);
    server.Publish(manifest, key);

    var check = await UpdateService.CheckAsync(server.Feed, trusted, new Version(1, 3, 2));
    Check(check.State == UpdateState.Available, $"expected an update, got {check.State}: {check.Error}");
    Check(check.Manifest?.Version == "1.4.0", "manifest version");
    await WithTemp(async dir =>
    {
        var reports = new List<double>();
        var path = await UpdateService.DownloadSetupAsync(check.Manifest!, new SyncProgress<double>(reports.Add), folder: dir);
        Check(Same(setup, path), "installer content differs");
        Check(Path.GetFileName(path) == "DownloadSpeedManager-Setup-1.4.0.exe", "installer name");
        Check(reports.Count > 0 && reports[^1] == 1, "progress reaches 100%");
    });

    // Key rotation: the app trusts both keys for a while, so releases signed with the new key still install.
    using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    server.Publish(manifest, next);
    var rotated = await UpdateService.CheckAsync(server.Feed, [.. trusted, Convert.ToBase64String(next.ExportSubjectPublicKeyInfo())], new Version(1, 3, 2));
    Check(rotated.State == UpdateState.Available, "a release signed with the second listed key is accepted");
}

static async Task UpdateRefused()
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    string[] trusted = [Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())];
    var current = new Version(1, 3, 2);
    using var server = new FeedServer();
    var manifest = FeedServer.Manifest("2.0.0", server.BaseUrl + "/setup.exe", [1, 2, 3]);

    const string unsigned = "isn't signed by the developer";
    async Task Expect(UpdateState state, string why, string? error = null)
    {
        var check = await UpdateService.CheckAsync(server.Feed, trusted, current);
        Check(check.State == state, $"{why} (got {check.State}: {check.Error})");
        Check(error is null || check.Error?.Contains(error) == true, $"{why}: unexpected reason '{check.Error}'");
        if (state == UpdateState.Failed) Check(check.Manifest is null, why + ": nothing from the manifest may be used");
    }

    server.Publish(manifest, key);
    await Expect(UpdateState.Available, "the correctly signed manifest is accepted");

    server.Publish(manifest, stranger);
    await Expect(UpdateState.Failed, "a manifest signed by another key is refused", unsigned);

    server.Publish(manifest, key);
    var signed = Encoding.UTF8.GetString(server.Files["/latest/latest.json"]);
    server.Files["/latest/latest.json"] = Encoding.UTF8.GetBytes(signed.Replace(server.BaseUrl + "/setup.exe", "https://evil.example/setup.exe"));
    await Expect(UpdateState.Failed, "a manifest changed after signing is refused", unsigned);

    server.Publish(manifest, key);
    server.Files["/latest/latest.json.sig"] = "not a signature"u8.ToArray();
    await Expect(UpdateState.Failed, "a malformed signature is refused", unsigned);

    server.Files.TryRemove("/latest/latest.json.sig", out _);
    await Expect(UpdateState.Failed, "a missing signature is refused", unsigned);

    server.Publish(FeedServer.Manifest("1.3.2", server.BaseUrl + "/setup.exe", [1]), key);
    await Expect(UpdateState.UpToDate, "the same version is not offered");
    server.Publish(FeedServer.Manifest("1.0.0", server.BaseUrl + "/setup.exe", [1]), key);
    await Expect(UpdateState.UpToDate, "an older, correctly signed release never downgrades");

    server.Files.Clear();
    var none = await UpdateService.CheckAsync(server.Feed, trusted, current);
    Check(none.State == UpdateState.Failed && none.Error == "No release has been published yet.", "no release yet: " + none.Error);
}

static async Task UpdateBadInstaller()
{
    var setup = RandomNumberGenerator.GetBytes(64 * KB);
    using var server = new FeedServer();
    var manifest = FeedServer.Manifest("1.4.0", server.BaseUrl + "/setup.exe", setup);
    await WithTemp(async dir =>
    {
        var altered = (byte[])setup.Clone();
        altered[100] ^= 0xFF;
        server.Files["/setup.exe"] = altered;
        await Throws<InvalidDataException>(() => UpdateService.DownloadSetupAsync(manifest, null, folder: dir), "an altered installer is rejected");
        Check(!Directory.Exists(dir) || Directory.GetFiles(dir).Length == 0, "the rejected installer is deleted");

        server.Files["/setup.exe"] = [.. setup, .. "appended"u8.ToArray()];
        await Throws<InvalidDataException>(() => UpdateService.DownloadSetupAsync(manifest, null, folder: dir), "a longer installer is rejected");

        var plainHttp = FeedServer.Manifest("1.4.0", "http://example.com/setup.exe", setup);
        await Throws<InvalidDataException>(() => UpdateService.DownloadSetupAsync(plainHttp, null, folder: dir), "plain HTTP downloads are refused");
    });
}

static Task UpdateCommand()
{
    Check(CommandLine.Parse(["--update"]).Type == "update", "--update");
    Check(CommandLine.Parse(["/update"]).Type == "update", "/update");
    Check(CommandLine.Parse(["--update", "https://example.com/a.zip"]).Type == "update", "a link doesn't turn an update into a download");
    Check(CommandLine.Parse(["https://example.com/a.zip"]).Type == "add", "a bare link still adds a download");
    return Task.CompletedTask;
}

static async Task Throws<T>(Func<Task> run, string message) where T : Exception
{
    try { await run(); }
    catch (T) { return; }
    throw new Exception(message + " (no exception)");
}

static DownloadItem New(string url, string dir, int connections, long speedLimit = 0, string? fileName = null) =>
    new(new DownloadRecord { Url = url, Folder = dir, Connections = connections, SpeedLimit = speedLimit, FileName = fileName });

static void Check(bool ok, string? message)
{
    if (!ok) throw new Exception(message ?? "check failed");
}

static bool Same(byte[] body, string path) => SHA256.HashData(body).SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)));

static string[] Names(string dir) =>
    [.. Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal)];

static async Task WaitUntil(Func<bool> condition, string what, int seconds = 15)
{
    var deadline = Stopwatch.StartNew();
    while (!condition())
    {
        if (deadline.Elapsed.TotalSeconds > seconds) throw new TimeoutException($"timed out waiting for {what}");
        await Task.Delay(20);
    }
}

static async Task WithTemp(Func<string, Task> run)
{
    var dir = Path.Combine(Path.GetTempPath(), "dsm-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try { await run(dir); }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
