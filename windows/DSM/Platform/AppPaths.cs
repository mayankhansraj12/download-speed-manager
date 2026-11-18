using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsm.Platform;

public static class AppPaths
{
    public const string AppName = "Download Speed Manager";
    public const string AppId = "DownloadSpeedManager";

    /// <summary>Roaming data: settings and the download list.</summary>
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId);

    /// <summary>Machine-local data: browser integration manifests.</summary>
    public static string LocalDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppId);

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppId + ".exe");
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Single-line JSON for pipe and native-messaging messages.</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    /// <summary>Write via a temporary file so a crash never leaves half a file behind.</summary>
    public static void WriteAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null; }
        catch { return null; }
    }
}
