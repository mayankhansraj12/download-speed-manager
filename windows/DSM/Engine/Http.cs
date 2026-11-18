using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Dsm.Engine;

public sealed class HttpStatusError(int status, string host) : IOException($"HTTP {status} from {host}")
{
    public int Status { get; } = status;
    public bool Retryable => Status is 408 or 429 || Status >= 500;
}

public sealed class DownloadError(string message, bool retryable = false) : IOException(message)
{
    public bool Retryable { get; } = retryable;
}

public sealed record ProbeResult(Uri FinalUri, long? Size, bool Resumable, string? ETag, string? LastModified,
    string? ContentType, string FileName);

public static partial class Http
{
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DownloadSpeedManager/1.0";

    /// <summary>Try IPv4 addresses first (as IDM does), falling back to IPv6.</summary>
    public static bool PreferIPv4 { get; set; } = true;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, // followed manually so credentials never leak across sites
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        MaxConnectionsPerServer = 256,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
        MaxResponseDrainSize = 64 * 1024,
        ConnectCallback = ConnectAsync,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static bool IsRetryable(Exception ex) => ex switch
    {
        HttpStatusError status => status.Retryable,
        DownloadError error => error.Retryable,
        _ => true, // socket resets, DNS hiccups and timeouts are worth retrying
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
        IEnumerable<IPAddress> ordered = PreferIPv4
            ? addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            : addresses;

        Exception? last = null;
        foreach (var address in ordered)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(address, port, attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>GET with manual redirects. The response body is not read.</summary>
    public static async Task<(HttpResponseMessage Response, Uri FinalUri)> GetAsync(Uri url,
        IReadOnlyDictionary<string, string> headers, long? from, long? to, string? ifRange, CancellationToken ct)
    {
        var uri = url;
        var firstAuthority = url.GetLeftPart(UriPartial.Authority);
        var sendCredentials = true;
        for (var hop = 0; ; hop++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                // HTTP/1.1 only: HTTP/2 would multiplex every "connection" over one TCP stream.
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            var h = request.Headers;
            h.TryAddWithoutValidation("User-Agent", headers.GetValueOrDefault("user-agent") ?? UserAgent);
            h.TryAddWithoutValidation("Accept-Encoding", "identity");
            if (headers.TryGetValue("referer", out var referer)) h.TryAddWithoutValidation("Referer", referer);
            if (sendCredentials && headers.TryGetValue("cookie", out var cookie)) h.TryAddWithoutValidation("Cookie", cookie);
            if (sendCredentials && headers.TryGetValue("authorization", out var auth)) h.TryAddWithoutValidation("Authorization", auth);
            if (from is not null) h.Range = new RangeHeaderValue(from, to);
            if (ifRange is not null) h.TryAddWithoutValidation("If-Range", ifRange);

            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (hop >= 10) throw new DownloadError("Too many redirects");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (uri.GetLeftPart(UriPartial.Authority) != firstAuthority) sendCredentials = false;
                continue;
            }
            return (response, uri);
        }
    }

    /// <summary>Discover size, range support, validators and a file name without downloading the body.</summary>
    public static async Task<ProbeResult> ProbeAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var (response, finalUri) = await GetAsync(url, headers, 0, 0, null, ct);
        using (response)
        {
            var code = (int)response.StatusCode;
            if (code >= 400 && code != 416) throw new HttpStatusError(code, finalUri.Host);
            long? size = null;
            var resumable = false;
            if (code is 206 or 416 && response.Content.Headers.ContentRange?.Length is long total)
            {
                size = total;
                resumable = true;
            }
            else if (code == 200)
            {
                size = response.Content.Headers.ContentLength;
            }
            return new ProbeResult(finalUri, size, resumable, response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R"),
                response.Content.Headers.ContentType?.MediaType, FileNameFrom(response, finalUri));
        }
    }

    public static string FileNameFrom(HttpResponseMessage response, Uri uri)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var name = (disposition?.FileNameStar ?? disposition?.FileName)?.Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(name)) name = FileNameFromUrl(uri);
        return Sanitize(name);
    }

    public static string FileNameFromUrl(Uri uri)
    {
        var last = uri.Segments.LastOrDefault()?.TrimEnd('/') ?? "";
        try { return Uri.UnescapeDataString(last); } catch (UriFormatException) { return last; }
    }

    [GeneratedRegex(@"^(con|prn|aux|nul|com\d|lpt\d)(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedName();

    /// <summary>Make a name safe for Windows file systems.</summary>
    public static string Sanitize(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder();
        foreach (var c in name ?? "") builder.Append(c < 32 || invalid.Contains(c) ? '_' : c);
        var clean = builder.ToString().Trim().TrimEnd('.', ' ');
        if (clean.Length == 0) clean = "download";
        if (ReservedName().IsMatch(clean)) clean = "_" + clean;
        return clean.Length > 200 ? clean[..200] : clean;
    }

    /// <summary>A short, human-readable reason for an exception.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };
}
