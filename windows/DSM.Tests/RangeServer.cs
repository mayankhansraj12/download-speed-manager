using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dsm.Tests;

/// <summary>
/// Minimal keep-alive HTTP/1.1 file server for engine tests. /file honours Range,
/// /norange ignores it, /redirect points at /file. Behaviour hooks simulate slow,
/// dead and connection-capped servers.
/// </summary>
internal sealed class RangeServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _body;
    private int _serving;
    private int _maxServing;
    private long _bytesServed;

    public RangeServer(byte[] body)
    {
        _body = body;
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public Func<long, int> DelayFor { get; init; } = _ => 0;   // ms per 64 KiB chunk, by start offset
    public Func<long, bool> StallFor { get; init; } = _ => false; // go silent after the first chunk
    public int MaxConnections { get; init; } = int.MaxValue;     // answer 429 beyond this many transfers

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
    public int MaxServing => _maxServing;
    public long BytesServed => Interlocked.Read(ref _bytesServed);

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
            var stream = client.GetStream();
            try
            {
                while (await ReadHeadAsync(stream) is { } head)
                {
                    if (!await RespondAsync(stream, head)) return;
                }
            }
            catch
            {
                // Client went away.
            }
        }
    }

    private async Task<bool> RespondAsync(NetworkStream stream, string head)
    {
        var lines = head.Split("\r\n");
        var path = lines[0].Split(' ')[1];
        var range = lines.FirstOrDefault(l => l.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));

        if (path == "/redirect")
        {
            await WriteAsync(stream, "HTTP/1.1 302 Found\r\nLocation: /file\r\nContent-Length: 0\r\n\r\n");
            return true;
        }

        var serving = Interlocked.Increment(ref _serving);
        try
        {
            if (serving > MaxConnections)
            {
                await WriteAsync(stream, "HTTP/1.1 429 Too Many Requests\r\nContent-Length: 0\r\n\r\n");
                return true;
            }
            InterlockedMax(ref _maxServing, serving);

            var ranges = path != "/norange";
            long start = 0, end = _body.Length - 1;
            var status = "200 OK";
            var extra = new StringBuilder();
            if (ranges) extra.Append("Accept-Ranges: bytes\r\n");
            if (ranges && range is not null)
            {
                var spec = range[(range.IndexOf('=') + 1)..].Trim().Split('-');
                start = long.Parse(spec[0]);
                if (spec[1].Length > 0) end = Math.Min(long.Parse(spec[1]), _body.Length - 1);
                status = "206 Partial Content";
                extra.Append($"Content-Range: bytes {start}-{end}/{_body.Length}\r\n");
            }
            await WriteAsync(stream,
                $"HTTP/1.1 {status}\r\nContent-Type: application/octet-stream\r\n" +
                $"Content-Disposition: attachment; filename=\"test file.bin\"\r\nETag: \"v1\"\r\n{extra}" +
                $"Content-Length: {end - start + 1}\r\n\r\n");

            var delay = DelayFor(start);
            var stall = StallFor(start);
            for (var i = start; i <= end; i += 64 * 1024)
            {
                if (stall && i > start)
                {
                    _ = await stream.ReadAsync(new byte[1], _cts.Token); // silent until the client hangs up
                    return false;
                }
                var count = (int)Math.Min(64 * 1024, end - i + 1);
                await stream.WriteAsync(_body.AsMemory((int)i, count), _cts.Token);
                Interlocked.Add(ref _bytesServed, count);
                if (delay > 0) await Task.Delay(delay, _cts.Token);
            }
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _serving);
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            bytes.Add(one[0]);
            var n = bytes.Count;
            if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n' && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
    }

    private async Task WriteAsync(NetworkStream stream, string text) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), _cts.Token);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
