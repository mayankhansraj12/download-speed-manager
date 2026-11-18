using System.Text.Json;
using Dsm.Platform;
using Microsoft.Win32;

namespace Dsm.Integration;

/// <summary>Registers the native-messaging host for the bundled browser extension (per user, no admin).</summary>
internal static class BrowserIntegration
{
    /// <summary>ID of the bundled extension, fixed by the public key in its manifest.</summary>
    public const string ChromeExtensionId = "glhkcjbcocbmlopkkbepbchghfkmplob";
    public const string FirefoxExtensionId = "capture@download-speed-manager.local";

    private static readonly (string Browser, string Key)[] Browsers =
    [
        ("Chrome", @"Software\Google\Chrome\NativeMessagingHosts\"),
        ("Edge", @"Software\Microsoft\Edge\NativeMessagingHosts\"),
        ("Brave", @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\"),
        ("Firefox", @"Software\Mozilla\NativeMessagingHosts\"),
    ];

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    private static string ManifestFolder => Path.Combine(AppPaths.LocalDir, "native-messaging");

    private static string ManifestFor(string browser) =>
        Path.Combine(ManifestFolder, browser == "Firefox" ? "firefox.json" : "chromium.json");

    public static void Register()
    {
        Directory.CreateDirectory(ManifestFolder);
        var common = new Dictionary<string, object>
        {
            ["name"] = NativeHost.Name,
            ["description"] = "Download Speed Manager browser integration",
            ["path"] = AppPaths.ExecutablePath,
            ["type"] = "stdio",
        };
        Write(ManifestFor("Chrome"), new Dictionary<string, object>(common)
        {
            ["allowed_origins"] = new[] { $"chrome-extension://{ChromeExtensionId}/" },
        });
        Write(ManifestFor("Firefox"), new Dictionary<string, object>(common)
        {
            ["allowed_extensions"] = new[] { FirefoxExtensionId },
        });
        foreach (var (browser, key) in Browsers)
        {
            using var registryKey = Registry.CurrentUser.CreateSubKey(key + NativeHost.Name);
            registryKey.SetValue("", ManifestFor(browser));
        }
    }

    public static void Unregister()
    {
        foreach (var (_, key) in Browsers) Registry.CurrentUser.DeleteSubKeyTree(key + NativeHost.Name, throwOnMissingSubKey: false);
    }

    public static IEnumerable<(string Browser, bool Registered)> Status() =>
        Browsers.Where(b => b.Browser != "Brave").Select(b => (b.Browser, IsRegistered(b.Key)));

    private static bool IsRegistered(string key)
    {
        using var registryKey = Registry.CurrentUser.OpenSubKey(key + NativeHost.Name);
        return registryKey?.GetValue("") is string path && File.Exists(path);
    }

    /// <summary>The extension folder shipped next to the app (or in the source tree during development).</summary>
    public static string? ExtensionFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "extension");
            if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        }
        return null;
    }

    private static void Write(string path, Dictionary<string, object> manifest) =>
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, ManifestJson));
}
