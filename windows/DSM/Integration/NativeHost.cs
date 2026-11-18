using System.Diagnostics;
using System.Text.Json;
using Dsm.Platform;

namespace Dsm.Integration;

/// <summary>
/// Browser native-messaging host. Chrome and Edge start the app with the extension's origin
/// as an argument; Firefox passes the manifest path. Messages are JSON with a 32-bit length
/// prefix on stdin/stdout. The host relays downloads to the running app (starting it when
/// needed): the same relay design REA found in IDM's IDMMsgHost.exe.
/// </summary>
internal static class NativeHost
{
    public const string Name = "com.downloadspeedmanager.host";
    private const int MaxMessage = 8 * 1024 * 1024; // a link batch with cookies can be large

    public static bool IsInvocation(string[] args) =>
        args.Any(a => a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) ||
        (args.Length >= 2 && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(args[0]));

    public static int Run()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        while (Read(input) is { } message)
        {
            object reply;
            try { reply = Handle(message); }
            catch (Exception ex) { reply = new { ok = false, error = ex.Message }; }
            Write(output, reply);
        }
        return 0;
    }

    private static object Handle(JsonElement message)
    {
        switch (Str(message, "type"))
        {
            case "ping":
                return new { ok = true, app = AppPaths.AppId, version = "1.0" };

            case "download":
                // One download. Silent ones go straight to the queue; others open the New download dialog.
                if (HttpUrl(Str(message, "url")) is not { } url) return new { ok = false, error = "Only http and https links are supported" };
                if (!EnsureAppRunning()) return new { ok = false, error = "Download Speed Manager could not be started" };
                return new { ok = SingleInstance.Send(AddCommand(message, message, url, Flag(message, "silent")), 5000) };

            case "batch":
                // Many links picked in the browser ("Download all links"): queued without a dialog each.
                if (!message.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return new { ok = false, error = "No links were sent" };
                if (!EnsureAppRunning()) return new { ok = false, error = "Download Speed Manager could not be started" };
                var sent = 0;
                foreach (var item in items.EnumerateArray().Take(MaxBatch))
                {
                    if (HttpUrl(Str(item, "url")) is { } link && SingleInstance.Send(AddCommand(message, item, link, silent: true), 5000)) sent++;
                }
                return new { ok = sent > 0, sent, error = sent > 0 ? null : "The app did not accept the links" };

            default:
                return new { ok = false, error = "Unknown message" };
        }
    }

    private const int MaxBatch = 1000;

    private static AppCommand AddCommand(JsonElement message, JsonElement item, string url, bool silent) => new()
    {
        Type = "add",
        Url = url,
        FileName = Str(item, "fileName"),
        Cookies = Str(item, "cookies"),
        Referrer = Str(item, "referrer") ?? Str(message, "referrer"),
        UserAgent = Str(message, "userAgent"),
        Silent = silent,
        StartNow = !Flag(message, "later"),
    };

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? HttpUrl(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;

    private static bool EnsureAppRunning()
    {
        if (SingleInstance.IsRunning()) return true;
        // ShellExecute: the app must not inherit the browser's stdio pipes.
        Process.Start(new ProcessStartInfo(AppPaths.ExecutablePath, "--tray") { UseShellExecute = true });
        for (var i = 0; i < 50; i++)
        {
            Thread.Sleep(200);
            if (SingleInstance.Send(new AppCommand { Type = "ping" }, 500)) return true;
        }
        return false;
    }

    private static JsonElement? Read(Stream input)
    {
        var header = new byte[4];
        if (!TryReadExactly(input, header)) return null;
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxMessage) return null;
        var body = new byte[length];
        if (!TryReadExactly(input, body)) return null;
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static bool TryReadExactly(Stream input, byte[] buffer)
    {
        try
        {
            input.ReadExactly(buffer);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    private static void Write(Stream output, object reply)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(reply);
        output.Write(BitConverter.GetBytes(body.Length));
        output.Write(body);
        output.Flush();
    }
}
