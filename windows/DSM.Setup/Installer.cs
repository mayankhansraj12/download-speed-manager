using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace Dsm.Setup;

/// <summary>Per-user install and uninstall of Download Speed Manager. No administrator rights needed.</summary>
internal static class Installer
{
    public const string AppName = "Download Speed Manager";
    public const string AppExe = "DownloadSpeedManager.exe";
    public const string UninstallerExe = "Uninstall.exe";
    private const string AppId = "DownloadSpeedManager";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string NativeHost = "com.downloadspeedmanager.host";
    private static readonly string[] Browsers =
        [@"Google\Chrome", @"Microsoft\Edge", @"BraveSoftware\Brave-Browser", "Mozilla"];

    public sealed record ExistingInstall(string Folder, string? Version);

    public static string Version => typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");

    private static string DesktopShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk");

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo("payload.zip") is not null;

    /// <summary>The installation recorded in Settings → Apps, if any.</summary>
    public static ExistingInstall? FindExisting()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") is string folder && Directory.Exists(folder)
            ? new ExistingInstall(folder, key.GetValue("DisplayVersion") as string)
            : null;
    }

    public static bool HasDesktopShortcut => File.Exists(DesktopShortcut);

    /// <param name="desktopShortcut">null keeps the user's current choice (silent installs and updates).</param>
    public static void Install(string folder, bool? desktopShortcut, IProgress<(int Percent, string Message)> progress)
    {
        folder = Path.GetFullPath(folder);
        progress.Report((5, "Closing Download Speed Manager if it is running…"));
        CloseRunningApp(folder);

        progress.Report((15, "Copying files…"));
        Directory.CreateDirectory(folder);
        ExtractPayload(folder, percent => progress.Report((15 + percent * 60 / 100, "Copying files…")));

        progress.Report((80, "Adding the uninstaller…"));
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate the setup program.");
        var uninstaller = Path.Combine(folder, UninstallerExe);
        if (!string.Equals(Path.GetFullPath(self), uninstaller, StringComparison.OrdinalIgnoreCase)) Retry(() => File.Copy(self, uninstaller, overwrite: true));

        progress.Report((88, "Creating shortcuts…"));
        var exe = Path.Combine(folder, AppExe);
        CreateShortcut(StartMenuShortcut, exe, folder);
        if (desktopShortcut ?? HasDesktopShortcut) CreateShortcut(DesktopShortcut, exe, folder);
        else TryDelete(DesktopShortcut);

        progress.Report((95, "Adding Download Speed Manager to Settings → Apps…"));
        RegisterUninstall(folder, exe);
        progress.Report((100, "Installed"));
    }

    public static void Uninstall(string folder, bool removeData, IProgress<(int Percent, string Message)> progress)
    {
        folder = Path.GetFullPath(folder);
        progress.Report((5, "Closing Download Speed Manager…"));
        CloseRunningApp(folder);

        progress.Report((35, "Removing shortcuts…"));
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);

        progress.Report((50, "Removing Windows and browser registrations…"));
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)) run?.DeleteValue(AppId, throwOnMissingValue: false);
        foreach (var browser in Browsers)
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\{browser}\NativeMessagingHosts\{NativeHost}", throwOnMissingSubKey: false);
        TryDeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppId));
        if (removeData) TryDeleteDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppId));

        progress.Report((75, "Removing files…"));
        RemoveInstalledFiles(folder);
        progress.Report((100, "Removed"));
    }

    public static void Launch(string folder) =>
        Process.Start(new ProcessStartInfo(Path.Combine(folder, AppExe)) { UseShellExecute = true, WorkingDirectory = folder });

    /// <summary>Ask a running copy to pause its downloads and exit; force it only if it doesn't.</summary>
    public static void CloseRunningApp(string folder)
    {
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)).Where(p => IsUnder(p, folder)).ToList();
        if (running.Count == 0) return;
        var exe = Path.Combine(folder, AppExe);
        try
        {
            using var exit = Process.Start(new ProcessStartInfo(exe, "--exit") { UseShellExecute = false, CreateNoWindow = true });
            exit?.WaitForExit(5000);
        }
        catch
        {
            // Older copies may not understand --exit; they are closed below.
        }
        foreach (var process in running)
        {
            try
            {
                if (!process.WaitForExit(10_000))
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>A copy of the app running from somewhere other than <paramref name="folder"/>, if any.</summary>
    public static string? RunningElsewhere(string folder)
    {
        folder = Path.GetFullPath(folder);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)))
        {
            using (process)
            {
                string? path;
                try { path = process.MainModule?.FileName; }
                catch { continue; }
                if (path is not null && !path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return path;
            }
        }
        return null;
    }

    private static bool IsUnder(Process process, string folder)
    {
        try { return process.MainModule?.FileName?.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true; }
        catch { return false; }
    }

    private static ZipArchive OpenPayload()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("This setup file is incomplete: the app files are missing.");
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static void ExtractPayload(string folder, Action<int> percent)
    {
        using var zip = OpenPayload();
        var root = folder + Path.DirectorySeparatorChar;
        var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            var target = Path.GetFullPath(Path.Combine(folder, entries[i].FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Unsafe path in setup data: {entries[i].FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var entry = entries[i];
            Retry(() => entry.ExtractToFile(target, overwrite: true)); // the app may take a moment to release its files
            percent((i + 1) * 100 / entries.Count);
        }
    }

    /// <summary>Delete only what setup installed (payload files and the uninstaller), then empty folders.</summary>
    private static void RemoveInstalledFiles(string folder)
    {
        var files = new List<string> { UninstallerExe };
        if (HasPayload)
        {
            using var zip = OpenPayload();
            files.AddRange(zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName));
        }
        else
        {
            files.Add(AppExe);
        }
        foreach (var relative in files)
        {
            var path = Path.GetFullPath(Path.Combine(folder, relative));
            if (path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) TryDelete(path);
        }
        if (!Directory.Exists(folder)) return;
        foreach (var dir in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) TryDeleteDirectory(dir);
        }
        if (!Directory.EnumerateFileSystemEntries(folder).Any()) TryDeleteDirectory(folder);
    }

    private static void RegisterUninstall(string folder, string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        var uninstaller = Path.Combine(folder, UninstallerExe);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", AppName);
        key.SetValue("DisplayIcon", exe + ",0");
        key.SetValue("InstallLocation", folder);
        key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" /uninstall /S");
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        var sizeKb = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024;
        key.SetValue("EstimatedSize", (int)sizeKb, RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void CreateShortcut(string path, string target, string folder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(folder);
            link.SetIconLocation(target, 0);
            link.SetDescription("Multi-connection download manager");
            ((IPersistFile)link).Save(path, fRemember: false);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(300);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxIconPath, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
