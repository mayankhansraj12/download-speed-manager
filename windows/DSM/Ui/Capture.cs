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
                new OptionsForm(shell, OptionsTab.General).Show();
                new OptionsForm(shell, OptionsTab.Browser).Show();
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

    private static void SaveAll(string folder)
    {
        Directory.CreateDirectory(folder);
        var index = 0;
        foreach (var form in Application.OpenForms.Cast<Form>().Where(f => f.Visible).ToList())
        {
            using var bitmap = new Bitmap(form.Width, form.Height);
            using (var g = Graphics.FromImage(bitmap))
            {
                var hdc = g.GetHdc();
                PrintWindow(form.Handle, hdc, RenderFullContent);
                g.ReleaseHdc(hdc);
            }
            bitmap.Save(Path.Combine(folder, $"{++index}-{form.GetType().Name}.png"), ImageFormat.Png);
        }
    }
}
#endif
