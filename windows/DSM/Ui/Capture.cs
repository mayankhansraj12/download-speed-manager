#if DEBUG
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Dsm.Ui;

/// <summary>Debug builds only: open the main windows, screenshot them with PrintWindow, then exit.</summary>
internal static partial class Capture
{
    private const uint RenderFullContent = 2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);

    public static void Schedule(Shell shell, string folder)
    {
        var step = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += (_, _) =>
        {
            step++;
            if (step == 1)
            {
                if (shell.Queue.Items.FirstOrDefault(i => i.IsActive) is { } running) shell.ShowProgress(running);
                // DSM_CAPTURE_UPDATE=1.1.0 shows the update bar as it appears when that version is released.
                if (Environment.GetEnvironmentVariable("DSM_CAPTURE_UPDATE") is { Length: > 0 } version)
                {
                    var available = new Dsm.Updates.UpdateCheck(Dsm.Updates.UpdateState.Available, new Dsm.Updates.UpdateManifest { Version = version });
                    Application.OpenForms.OfType<MainForm>().FirstOrDefault()?.ShowUpdate(available, portable: false);
                }
                timer.Interval = 2500;
            }
            else if (step == 2)
            {
                foreach (var form in Application.OpenForms.OfType<ProgressForm>()) form.ToggleConnections();
                shell.ShowAddDialog(shell.Queue.Items.FirstOrDefault()?.Url);
                timer.Interval = 2500;
            }
            else if (step == 3)
            {
                // DSM_CAPTURE_OPTIONS=Updates (comma-separated) picks which Options pages to capture.
                var tabs = Environment.GetEnvironmentVariable("DSM_CAPTURE_OPTIONS") is { Length: > 0 } names
                    ? names.Split(',').Select(n => Enum.Parse<OptionsTab>(n.Trim(), ignoreCase: true))
                    : [OptionsTab.General, OptionsTab.Browser];
                foreach (var tab in tabs) new OptionsForm(shell, tab).Show();
                var done = shell.Queue.Items.FirstOrDefault(i => i.Status == Dsm.Engine.DownloadStatus.Completed);
                if (done is not null) new CompletedForm(shell, done).Show();
                timer.Interval = 2000;
            }
            else
            {
                timer.Stop();
                SaveAll(folder);
                shell.Exit();
            }
        };
        timer.Start();
    }

    /// <summary>Record the progress window of the first running download as numbered frames (for an animation).</summary>
    public static void ScheduleFrames(Shell shell, string folder)
    {
        Directory.CreateDirectory(folder);
        ProgressForm? form = null;
        var frame = 0;
        var framesAfterDone = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            if (form is null)
            {
                if (shell.Queue.Items.FirstOrDefault(i => i.IsActive) is not { } item) return;
                shell.ShowProgress(item);
                form = Application.OpenForms.OfType<ProgressForm>().FirstOrDefault();
                if (form is null) return;
                form.KeepOpenWhenDone(); // end the recording on the finished state
                form.ToggleConnections();
                return;
            }
            Save(form, Path.Combine(folder, $"frame-{++frame:D4}.png"));
            var done = shell.Queue.Items.All(i => !i.IsActive);
            if ((done && ++framesAfterDone > 12) || frame > 900)
            {
                timer.Stop();
                shell.Exit();
            }
        };
        timer.Start();
    }

    private static void SaveAll(string folder)
    {
        Directory.CreateDirectory(folder);
        var index = 0;
        foreach (var form in Application.OpenForms.Cast<Form>().Where(f => f.Visible).ToList())
            Save(form, Path.Combine(folder, $"{++index}-{form.GetType().Name}.png"));
    }

    private static void Save(Form form, string path)
    {
        using var bitmap = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            PrintWindow(form.Handle, hdc, RenderFullContent);
            g.ReleaseHdc(hdc);
        }
        bitmap.Save(path, ImageFormat.Png);
    }
}
#endif
