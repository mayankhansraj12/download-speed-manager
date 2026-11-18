using System.Diagnostics;

namespace Dsm.Setup;

/// <summary>
/// Command line:
///   (none)                    install wizard
///   /S [/D=folder] [/desktop | /nodesktop] [/run]   silent install (no flag keeps the current desktop shortcut)
///   /uninstall [/S] [/removedata]      uninstall (wizard or silent)
/// </summary>
internal sealed class SetupOptions
{
    public bool Uninstall { get; private set; }
    public bool Silent { get; private set; }
    public bool FromTemp { get; private set; }
    public bool? DesktopShortcut { get; private set; } // null: keep what the user has (updates)
    public bool Run { get; private set; }
    public bool RemoveData { get; private set; }
    public string? Folder { get; private set; }
    public int? ParentPid { get; private set; }
    public string? CaptureDir { get; private set; }

    public static SetupOptions Parse(string[] args)
    {
        var options = new SetupOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var lower = arg.ToLowerInvariant();
            if (lower is "/uninstall" or "--uninstall") options.Uninstall = true;
            else if (lower is "/s" or "/silent" or "/quiet" or "--silent") options.Silent = true;
            else if (lower == "/from-temp") options.FromTemp = true;
            else if (lower == "/desktop") options.DesktopShortcut = true;
            else if (lower == "/nodesktop") options.DesktopShortcut = false;
            else if (lower == "/run") options.Run = true;
            else if (lower == "/removedata") options.RemoveData = true;
            else if (lower.StartsWith("/d=") || lower.StartsWith("/dir=")) options.Folder = arg[(arg.IndexOf('=') + 1)..].Trim('"');
            else if (lower.StartsWith("/parent=") && int.TryParse(arg[8..], out var pid)) options.ParentPid = pid;
            else if (lower == "/capture" && i + 1 < args.Length) options.CaptureDir = args[++i];
        }
        return options;
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = SetupOptions.Parse(args);

        // The uninstaller lives inside the folder it removes, so it hands over to a temporary copy.
        if (options.Uninstall && !options.FromTemp && options.CaptureDir is null) return RelaunchFromTemp(options);
        if (options.ParentPid is int parent) WaitForExit(parent);

        try
        {
            if (options.Silent) return RunSilent(options);
            ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // WinForms dark mode is marked experimental
            Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
            var form = new SetupForm(options);
#if DEBUG
            if (options.CaptureDir is { } dir) form.Shown += async (_, _) => await form.CaptureAsync(dir);
#endif
            Application.Run(form);
            return 0;
        }
        finally
        {
            if (options.FromTemp) DeleteSelfLater();
        }
    }

    private static int RunSilent(SetupOptions options)
    {
        try
        {
            if (options.Uninstall)
            {
                var folder = options.Folder ?? Installer.FindExisting()?.Folder
                    ?? throw new InvalidOperationException("Download Speed Manager is not installed.");
                Installer.Uninstall(folder, options.RemoveData, NoProgress.Instance);
            }
            else
            {
                var folder = options.Folder ?? Installer.FindExisting()?.Folder ?? Installer.DefaultFolder;
                Installer.Install(folder, options.DesktopShortcut, NoProgress.Instance);
                if (options.Run) Installer.Launch(folder);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log(ex);
            return 1;
        }
    }

    private static int RelaunchFromTemp(SetupOptions options)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate the uninstaller.");
        var folder = options.Folder ?? Installer.FindExisting()?.Folder ?? Path.GetDirectoryName(self)!;
        var temp = Path.Combine(Path.GetTempPath(), $"DownloadSpeedManager-uninstall-{Guid.NewGuid():N}.exe");
        File.Copy(self, temp);
        var arguments = $"/uninstall /from-temp \"/dir={folder}\" /parent={Environment.ProcessId}";
        if (options.Silent) arguments += " /S";
        if (options.RemoveData) arguments += " /removedata";
        Process.Start(new ProcessStartInfo(temp, arguments) { UseShellExecute = false });
        return 0;
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit(15_000);
        }
        catch (ArgumentException)
        {
            // Already exited.
        }
    }

    /// <summary>A temporary uninstaller copy deletes itself a few seconds after it exits.</summary>
    private static void DeleteSelfLater()
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 4 >nul & del /f /q \"{Environment.ProcessPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch
        {
            // Windows cleans the temp folder eventually.
        }
    }

    public static void Log(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "DownloadSpeedManager-setup.log"), $"{DateTime.Now:u} {ex}\n\n"); }
        catch { /* nothing else to do */ }
    }

    private sealed class NoProgress : IProgress<(int Percent, string Message)>
    {
        public static readonly NoProgress Instance = new();

        public void Report((int Percent, string Message) value) { }
    }
}
