using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Dsm.Platform;

namespace Dsm.Integration;

/// <summary>One running app per user: later launches hand their command over a named pipe.</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        IsFirst = created;
    }

    public bool IsFirst { get; }

    /// <summary>Raised on a background thread for each command another process sends.</summary>
    public event Action<AppCommand>? CommandReceived;

    // Scoped to the Windows account and data folder, so test instances never collide with the real one.
    private static string Suffix
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var data = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DataDir.ToLowerInvariant())))[..8];
            return $"{sid}-{data}";
        }
    }

    private static string MutexName => $@"Local\{AppPaths.AppId}-{Suffix}";
    private static string PipeName => $"{AppPaths.AppId}-{Suffix}";

    public static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var existing)) return false;
        existing.Dispose();
        return true;
    }

    public void Listen() => _ = Task.Run(ListenLoopAsync);

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(_cts.Token);
                var command = line is null ? null : JsonSerializer.Deserialize<AppCommand>(line, Json.Compact);
                if (command is not null && command.Type != "ping") CommandReceived?.Invoke(command);
                await writer.WriteLineAsync("ok");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(100); // a misbehaving client must not stop the listener
            }
        }
    }

    public static bool Send(AppCommand command, int timeoutMs = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            writer.WriteLine(JsonSerializer.Serialize(command, Json.Compact));
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            return reader.ReadLine() == "ok";
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* released elsewhere */ }
        }
        _mutex.Dispose();
    }
}
