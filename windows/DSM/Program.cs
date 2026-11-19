using Dsm.Engine;
using Dsm.Integration;
using Dsm.Platform;
using Dsm.Ui;

namespace Dsm;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("DSM_DATA_DIR") is { Length: > 0 } dataDir) AppPaths.DataDir = dataDir;

        // Started by a browser for the extension: speak native messaging on stdin/stdout, no UI.
        if (NativeHost.IsInvocation(args)) return NativeHost.Run();

        var command = CommandLine.Parse(args);
        using var instance = new SingleInstance();
        if (!instance.IsFirst)
        {
            // Already running: hand over the command (show the window, add a link, exit) and leave.
            return SingleInstance.Send(command) ? 0 : 1;
        }
        if (command.Type == "exit") return 0; // nothing running, nothing to close

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);

        var settings = AppSettings.Load();
        ApplyTheme(settings.Theme); // must happen before any window is created
        var queue = new DownloadQueue(settings);
        queue.Load();
        if (settings.BrowserIntegration) TryRun(BrowserIntegration.Register); // keeps the host path current
        if (settings.LaunchOnStartup) TryRun(() => Startup.Apply(true));

        using var shell = new Shell(settings, queue, startHidden: command.Tray);
        instance.CommandReceived += received => shell.Post(() => shell.HandleCommand(received));
        instance.Listen();
        shell.ResumeAfterUpdate(); // continue downloads an update interrupted
        if (command.Type is "add" or "update" || command.StartQueue) shell.HandleCommand(command);
#if DEBUG
        var capture = Array.IndexOf(args, "--capture");
        if (capture >= 0 && capture + 1 < args.Length) Capture.Schedule(shell, args[capture + 1]);
        var frames = Array.IndexOf(args, "--capture-frames");
        if (frames >= 0 && frames + 1 < args.Length) Capture.ScheduleFrames(shell, args[frames + 1]);
#endif
        Application.Run(shell);
        return 0;
    }

#pragma warning disable WFO5001 // WinForms dark mode is marked experimental
    private static void ApplyTheme(string theme)
    {
        Application.SetColorMode(theme switch
        {
            "Light" => SystemColorMode.Classic,
            "System" => SystemColorMode.System,
            _ => SystemColorMode.Dark,
        });
        Theme.Use(Application.IsDarkModeEnabled);
    }
#pragma warning restore WFO5001

    private static void TryRun(Action action)
    {
        try { action(); } catch { /* integration is optional */ }
    }

    private static void ReportCrash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "errors.log"), $"{DateTime.Now:u} {ex}\n\n");
        }
        catch
        {
            // Nothing else to do.
        }
        MessageBox.Show($"Something went wrong:\n\n{ex.Message}\n\nDetails were written to errors.log in the app's data folder.",
            AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
