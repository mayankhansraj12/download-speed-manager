using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Dsm.Platform;
using Microsoft.Win32;

namespace Dsm.Updates;

public enum UpdateState { NotConfigured, UpToDate, Available, Failed }

public sealed record UpdateCheck(UpdateState State, UpdateManifest? Manifest = null, string? Error = null)
{
    /// <summary>The running copy is older than the release's minimum version: the update fixes something important.</summary>
    public bool Required =>
        Manifest?.MinimumVersion is { } minimum && UpdateManifest.TryParseVersion(minimum, out var v) && UpdateService.CurrentVersion < v;
}

/// <summary>
/// Client side of the update pipeline:
///   1. fetch latest.json and latest.json.sig from the newest GitHub Release,
///   2. verify the signature with the public key built into the app (ECDSA P-256),
///   3. compare versions; if newer, download the installer and check its size and SHA-256,
///   4. run the installer silently over this installation; it closes, updates and restarts the app.
/// Nothing but these HTTPS requests leaves the computer (no telemetry).
/// </summary>
public static class UpdateService
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DownloadSpeedManager";

    // Declared before Client: static initializers run in this order and the client's User-Agent uses it.
    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateService).Assembly.GetName().Version);

    private static readonly HttpClient Client = CreateClient();

    /// <summary>GitHub owner/repo baked in by the release workflow; null for local builds.</summary>
    public static string? Repository =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value is { Length: > 0 } repository ? repository : null;

    /// <summary>Where latest.json lives. DSM_UPDATE_FEED overrides it for testing; signatures are still required.</summary>
    public static string? FeedUrl =>
        Environment.GetEnvironmentVariable("DSM_UPDATE_FEED") is { Length: > 0 } feed ? feed.TrimEnd('/')
        : Repository is { } repository ? $"https://github.com/{repository}/releases/latest/download"
        : null;

    public static string? ReleasesPage => Repository is { } repository ? $"https://github.com/{repository}/releases" : null;

    private static Version Normalize(Version? v) => v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
            MaxResponseContentBufferSize = 1024 * 1024, // latest.json is tiny; refuse anything big
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DownloadSpeedManager/{CurrentVersion}");
        return client;
    }

    public static Task<UpdateCheck> CheckAsync(CancellationToken ct = default) =>
        FeedUrl is { } feed
            ? CheckAsync(feed, [.. PublicKeys()], CurrentVersion, ct)
            : Task.FromResult(new UpdateCheck(UpdateState.NotConfigured));

    /// <summary>The check itself, with the feed, trusted keys and running version passed in (tests use their own).</summary>
    public static async Task<UpdateCheck> CheckAsync(string feed, IReadOnlyList<string> publicKeys, Version current, CancellationToken ct = default)
    {
        feed = feed.TrimEnd('/');
        try
        {
            var manifestBytes = await Client.GetByteArrayAsync($"{feed}/latest.json", ct);
            string signature;
            try { signature = (await Client.GetStringAsync($"{feed}/latest.json.sig", ct)).Trim(); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { signature = ""; } // unsigned
            if (!VerifySignature(manifestBytes, signature, publicKeys))
                return new UpdateCheck(UpdateState.Failed, Error: "The update information isn't signed by the developer, so it was ignored.");
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(manifestBytes, UpdateManifest.JsonOptions)
                ?? throw new InvalidDataException("The update information is empty.");
            if (!UpdateManifest.TryParseVersion(manifest.Version, out var latest))
                throw new InvalidDataException($"Unexpected version \"{manifest.Version}\".");
            // Only ever move forward: an old (even correctly signed) manifest can't downgrade anyone.
            return new UpdateCheck(latest > current ? UpdateState.Available : UpdateState.UpToDate, manifest);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var reason = ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound } ? "No release has been published yet." : ex.Message;
            return new UpdateCheck(UpdateState.Failed, Error: reason);
        }
    }

    /// <summary>True if any of the trusted public keys (base64 SubjectPublicKeyInfo) made this signature.</summary>
    public static bool VerifySignature(byte[] data, string signatureBase64, IEnumerable<string> publicKeys)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64); }
        catch (FormatException) { return false; }
        if (signature.Length == 0) return false;
        foreach (var publicKey in publicKeys)
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
                if (key.VerifyData(data, signature, HashAlgorithmName.SHA256)) return true;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                // A malformed key line; try the next one.
            }
        }
        return false;
    }

    /// <summary>One base64 key per line. Listing two lets you rotate the signing key without breaking updates.</summary>
    public static IEnumerable<string> PublicKeys()
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("update-public-key.txt");
        if (stream is null) yield break;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length > 0 && !line.StartsWith('#')) yield return line;
        }
    }

    /// <summary>The folder Setup installed this copy to, or null for a portable copy (which can't update itself).</summary>
    public static string? InstalledFolder()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (key?.GetValue("InstallLocation") is not string folder) return null;
        var mine = Path.GetDirectoryName(AppPaths.ExecutablePath);
        return mine is not null && string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetFullPath(mine).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? folder
            : null;
    }

    /// <summary>Download the installer named in the (already verified) manifest and check its size and SHA-256.</summary>
    public static async Task<string> DownloadSetupAsync(UpdateManifest manifest, IProgress<double>? progress,
        CancellationToken ct = default, string? folder = null)
    {
        var url = new Uri(manifest.Setup.Url);
        if (url.Scheme != Uri.UriSchemeHttps && !url.IsLoopback)
            throw new InvalidDataException("Updates are only downloaded over HTTPS.");
        // A fresh folder that holds nothing but the installer, so nothing else can be loaded next to it.
        folder ??= Path.Combine(Path.GetTempPath(), "DownloadSpeedManager-Update");
        if (Directory.Exists(folder))
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(url.AbsolutePath);
        if (string.IsNullOrEmpty(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = "DownloadSpeedManager-Setup.exe";
        var path = Path.Combine(folder, name);

        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = manifest.Setup.Size > 0 ? manifest.Setup.Size : response.Content.Headers.ContentLength ?? 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(path))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                received += read;
                if (manifest.Setup.Size > 0 && received > manifest.Setup.Size) break; // longer than published: reject below
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.AppendData(buffer, 0, read);
                if (total > 0) progress?.Report(Math.Min(1, (double)received / total));
            }
        }

        var hash = Convert.ToHexString(sha.GetHashAndReset());
        if ((manifest.Setup.Size > 0 && received != manifest.Setup.Size) || !hash.Equals(manifest.Setup.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidDataException("The downloaded update didn't match the published checksum, so it was deleted.");
        }
        return path;
    }

    /// <summary>Run the installer silently over this installation. It closes the app, updates it and starts it again.</summary>
    public static void RunSetup(string setupPath, string installFolder) =>
        Process.Start(new ProcessStartInfo(setupPath, $"/S /run \"/D={installFolder}\"") { UseShellExecute = true });
}
