using Dsm.Platform;

namespace Dsm;

public sealed class AppSettings
{
    public string DownloadFolder { get; set; } = Native.DownloadsPath();
    public bool SortByCategory { get; set; } = true;
    public int DefaultConnections { get; set; } = 8;
    public int MaxConcurrent { get; set; } = 3;
    public long GlobalSpeedLimit { get; set; } // bytes per second, 0 = unlimited
    public bool PreferIPv4 { get; set; } = true;
    public bool MonitorClipboard { get; set; } = true;
    public bool LaunchOnStartup { get; set; }
    public bool ShowProgressWindow { get; set; } = true;
    public bool ShowCompletionDialog { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool BrowserIntegration { get; set; } = true;
    public string Theme { get; set; } = "Dark"; // Dark, Light or System (follow Windows)
    public bool CheckForUpdates { get; set; } = true;
    public bool AutoInstallUpdates { get; set; } // install without asking when no downloads are running
    public int UpdateCheckHours { get; set; } = 6; // how often to check: 1, 6 or 24
    public DateTime? LastUpdateCheck { get; set; }

    private static string FilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    public static AppSettings Load() => Json.Read<AppSettings>(FilePath) ?? new AppSettings();

    public void Save() => Json.WriteAtomic(FilePath, this);

    /// <summary>Where a category's downloads are saved.</summary>
    public string FolderFor(string category) => SortByCategory ? Path.Combine(DownloadFolder, category) : DownloadFolder;
}
