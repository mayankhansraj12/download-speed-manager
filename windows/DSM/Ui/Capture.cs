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
        // DSM_CAPTURE_SELECT=1 first saves the main window's interactive states (two extra steps).
        var states = Environment.GetEnvironmentVariable("DSM_CAPTURE_SELECT") == "1";
        // DSM_CAPTURE_MAIN_SIZE=780x420 captures the main window at that size (logical pixels).
        if (Environment.GetEnvironmentVariable("DSM_CAPTURE_MAIN_SIZE")?.Split('x') is [var w, var h]
            && Application.OpenForms.OfType<MainForm>().FirstOrDefault() is { } main)
        {
            main.Size = new Size(main.LogicalToDeviceUnits(int.Parse(w)), main.LogicalToDeviceUnits(int.Parse(h)));
        }
        var timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += (_, _) =>
        {
            step++;
            if (states && step <= 2)
            {
                if (step == 1) SaveStates(folder);
                else SaveSearch(shell, folder);
                timer.Interval = 1500;
                return;
            }
            var stage = states ? step - 2 : step;
            if (stage == 1)
            {
                if (shell.Queue.Items.FirstOrDefault(i => i.IsActive) is { } running) shell.ShowProgress(running);
                // DSM_CAPTURE_UPDATE=1.1.0 shows the update bar as it appears when that version is released.
                if (Environment.GetEnvironmentVariable("DSM_CAPTURE_UPDATE") is { Length: > 0 } version)
                {
                    var available = new Dsm.Updates.UpdateCheck(Dsm.Updates.UpdateState.Available, new Dsm.Updates.UpdateManifest { Version = version });
                    var main = Application.OpenForms.OfType<MainForm>().FirstOrDefault();
                    main?.ShowUpdate(available, portable: false);
                    // DSM_CAPTURE_UPDATE_PROGRESS="Downloading the update… 42%" shows it while installing (buttons disabled).
                    if (Environment.GetEnvironmentVariable("DSM_CAPTURE_UPDATE_PROGRESS") is { Length: > 0 } progress) main?.ShowUpdateProgress(progress);
                }
                timer.Interval = 2500;
            }
            else if (stage == 2)
            {
                foreach (var form in Application.OpenForms.OfType<ProgressForm>()) form.ToggleConnections();
                shell.ShowAddDialog(shell.Queue.Items.FirstOrDefault()?.Url);
                timer.Interval = 2500;
            }
            else if (stage == 3)
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

    private static IEnumerable<Control> All(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    /// <summary>Act as if the mouse were over the middle of <paramref name="area"/> (WM_MOUSEMOVE).</summary>
    private static void MouseOver(Control control, Rectangle area)
    {
        var x = area.Left + Math.Min(area.Width / 2, 120);
        var y = area.Top + area.Height / 2;
        SendMessage(control.Handle, 0x0200, 0, (y << 16) | (x & 0xFFFF));
    }

    /// <summary>
    /// Main window with two rows selected (list focused, keyboard focus cues on), a hovered row,
    /// sidebar item and toolbar button; then the Downloads menu with an item highlighted.
    /// </summary>
    private static void SaveStates(string folder)
    {
        if (Application.OpenForms.OfType<MainForm>().FirstOrDefault() is not { } main) return;
        var controls = All(main).ToList();
        if (controls.OfType<ListView>().FirstOrDefault() is not { Items.Count: > 2 } list) return;
        SendMessage(list.Handle, 0x0128, 0x10002, 0); // WM_UPDATEUISTATE: UIS_CLEAR UISF_HIDEFOCUS, as after a key press
        list.Focus();
        list.Items[0].Selected = list.Items[1].Selected = true;
        list.Items[0].Focused = true;
        // Paint each change right away: a posted mouse-leave (the real pointer is elsewhere) must not undo the hover.
        MouseOver(list, list.Items[2].Bounds);
        list.Update();
        if (controls.OfType<TreeView>().FirstOrDefault() is { Nodes.Count: > 0 } tree && tree.Nodes[0].Nodes.Count > 3)
        {
            MouseOver(tree, tree.Nodes[0].Nodes[3].Bounds);
            tree.Update();
        }
        if (controls.OfType<ToolStrip>().FirstOrDefault(s => s is not MenuStrip and not StatusStrip) is { } toolbar)
        {
            toolbar.Items[0].Select();
            toolbar.Update();
        }
        Directory.CreateDirectory(folder);
        Save(main, Path.Combine(folder, "0-MainForm-selected.png"));
        if (controls.OfType<MenuStrip>().FirstOrDefault() is { Items.Count: > 1 } menu && menu.Items[1] is ToolStripMenuItem downloads)
        {
            downloads.ShowDropDown();
            downloads.DropDownItems.OfType<ToolStripMenuItem>().Skip(2).FirstOrDefault()?.Select();
            downloads.DropDown.Update();
            Save(downloads.DropDown, Path.Combine(folder, "0-Menu.png"));
            downloads.HideDropDown();
        }
        if (controls.OfType<SearchBox>().FirstOrDefault() is { } search)
        {
            search.FocusText();
            search.Text = "no such file";
        }
    }

    /// <summary>
    /// Main window searching for something that isn't there; then the search is cleared and the
    /// sidebar gets keyboard focus.
    /// </summary>
    private static void SaveSearch(Shell shell, string folder)
    {
        if (Application.OpenForms.OfType<MainForm>().FirstOrDefault() is not { } main) return;
        Application.DoEvents();
        Save(main, Path.Combine(folder, "0-MainForm-search.png"));
        if (All(main).OfType<SearchBox>().FirstOrDefault() is { } search) search.Text = "";
        if (All(main).OfType<TreeView>().FirstOrDefault() is { } tree)
        {
            SendMessage(tree.Handle, 0x0128, 0x10002, 0); // WM_UPDATEUISTATE: show focus cues
            tree.Focus();
            Application.DoEvents();
            Save(main, Path.Combine(folder, "0-MainForm-tree.png"));
        }
        // The window of a failed download too (saved with the others at the end).
        if (shell.Queue.Items.FirstOrDefault(i => i.Status == Dsm.Engine.DownloadStatus.Error) is { } failed) shell.ShowProgress(failed);
    }

    private static void SaveAll(string folder)
    {
        Directory.CreateDirectory(folder);
        var index = 0;
        foreach (var form in Application.OpenForms.Cast<Form>().Where(f => f.Visible).ToList())
            Save(form, Path.Combine(folder, $"{++index}-{form.GetType().Name}.png"));
    }

    private static void Save(Control window, string path)
    {
        using var bitmap = new Bitmap(window.Width, window.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            PrintWindow(window.Handle, hdc, RenderFullContent);
            g.ReleaseHdc(hdc);
        }
        bitmap.Save(path, ImageFormat.Png);
    }
}
#endif
