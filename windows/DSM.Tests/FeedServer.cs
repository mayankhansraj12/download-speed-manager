using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dsm.Updates;

namespace Dsm.Tests;

/// <summary>
/// Stands in for GitHub Releases in update tests: serves whatever is in <see cref="Files"/> (404 otherwise).
/// <see cref="Publish"/> writes latest.json and its signature the way the release workflow does.
/// </summary>
internal sealed class FeedServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public FeedServer()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
    public string Feed => BaseUrl + "/latest";

    public void Publish(UpdateManifest manifest, ECDsa key)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdateManifest.JsonOptions);
        Files["/latest/latest.json"] = json;
        Files["/latest/latest.json.sig"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(key.SignData(json, HashAlgorithmName.SHA256)));
    }

    public static UpdateManifest Manifest(string version, string setupUrl, byte[] setup) => new()
    {
        Version = version,
        PublishedAt = DateTimeOffset.UtcNow,
        Setup = new UpdateFile { Url = setupUrl, Sha256 = Convert.ToHexString(SHA256.HashData(setup)), Size = setup.Length },
    };

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(buffer) == 1)
                    head.Append((char)buffer[0]);
                var path = head.ToString().Split(' ') is { Length: > 1 } parts ? parts[1] : "/";
                var found = Files.TryGetValue(path, out var body);
                body ??= [];
                var status = found ? "200 OK" : "404 Not Found";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: application/octet-stream\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(body);
            }
            catch
            {
                // Client went away.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

/// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; would post to the thread pool).</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
