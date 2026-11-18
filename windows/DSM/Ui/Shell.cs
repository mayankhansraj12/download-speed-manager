using System.Diagnostics;
using Dsm.Engine;
using Dsm.Integration;
using Dsm.Platform;
using Dsm.Updates;

namespace Dsm.Ui;

/// <summary>Runs the app: main window, notification-area icon, dialogs, clipboard and incoming commands.</summary>
internal sealed class Shell : ApplicationContext
{
    private readonly MainForm _main;
    private readonly NotifyIcon _tray = new();
    private readonly ClipboardWatcher _clipboard = new();
    private readonly Dictionary<string, ProgressForm> _progress = [];
    private readonly HashSet<string> _quiet = [];
    private readonly System.Windows.Forms.Timer _trayTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 20_000 }; // first check 20 s after start
    private bool _hiddenNoticeShown;
    private UpdateCheck? _update;
    private bool _installingUpdate;
    private string? _notifiedVersion;

    public Shell(AppSettings settings, DownloadQueue queue, bool startHidden)
    {
        Settings = settings;
        Queue = queue;
        AppIcon = LoadIcon();
        _main = new MainForm(this);
        _ = _main.Handle; // lets background events reach the UI thread while the window is hidden
        BuildTray();
        _clipboard.Enabled = settings.MonitorClipboard;
        _clipboard.Filter.AlreadyInList = url =>
            Queue.Items.Any(d => d.Url == url && d.Status != DownloadStatus.Cancelled);
        _clipboard.LinkCopied += url => ShowAddDialog(new AddRequest(url));
        queue.ItemChanged += item => Post(() => OnItemChanged(item));
        queue.ItemCompleted += item => Post(() => OnItemCompleted(item));
        _trayTimer.Tick += (_, _) => UpdateTray();
        _trayTimer.Start();
        _updateTimer.Tick += async (_, _) => await AutoCheckForUpdatesAsync();
        if (settings.CheckForUpdates) _updateTimer.Start();
        if (!startHidden) ShowMain();
    }

    // ---- Updates ------------------------------------------------------------------------------

    private static string ResumeFile => Path.Combine(AppPaths.DataDir, "resume-after-update.json");

    private async Task AutoCheckForUpdatesAsync()
    {
        _updateTimer.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
        if (!Settings.CheckForUpdates || _installingUpdate) return;
        var check = await UpdateService.CheckAsync();
        if (check.State != UpdateState.Available) return;
        _update = check;
        var portable = UpdateService.InstalledFolder() is null;
        if (Settings.AutoInstallUpdates && !portable && !Queue.Items.Any(d => d.IsActive))
        {
            await InstallUpdateAsync();
            return;
        }
        _main.ShowUpdate(check, portable);
        if (_notifiedVersion == check.Manifest!.Version) return;
        _notifiedVersion = check.Manifest.Version;
        _tray.ShowBalloonTip(5000, "Update available", $"Download Speed Manager {check.Manifest.Version} is ready to install.", ToolTipIcon.Info);
    }

    /// <summary>Help → Check for updates: always answers, even when there is nothing new.</summary>
    public async Task CheckForUpdatesNowAsync()
    {
        var check = await UpdateService.CheckAsync();
        var current = UpdateService.CurrentVersion.ToString(3);
        switch (check.State)
        {
            case UpdateState.NotConfigured:
                Inform("No update source", "This copy was built on this computer, so it has no update source. Copies installed from the GitHub releases check for updates automatically.");
                break;
            case UpdateState.UpToDate:
                Inform("You're up to date", $"Download Speed Manager {current} is the latest version.");
                break;
            case UpdateState.Failed:
                ShowError("Couldn't check for updates", check.Error ?? "Unknown error.");
                break;
            case UpdateState.Available:
                _update = check;
                var portable = UpdateService.InstalledFolder() is null;
                _main.ShowUpdate(check, portable);
                var install = new TaskDialogButton(portable ? "Download" : "Install now");
                var page = new TaskDialogPage
                {
                    Caption = AppPaths.AppName,
                    Heading = $"Version {check.Manifest!.Version} is available",
                    Text = portable
                        ? $"You have {current}. This copy isn't installed, so download the new version from the releases page."
                        : $"You have {current}. Download Speed Manager will close, update and start again. Running downloads continue afterwards.",
                    Icon = TaskDialogIcon.Information,
                    Buttons = { install, new TaskDialogButton("Later") },
                };
                if (TaskDialog.ShowDialog(page) == install) await InstallUpdateAsync();
                break;
        }
    }

    /// <summary>Download, verify and run the new installer, then exit so it can replace the app.</summary>
    public async Task InstallUpdateAsync()
    {
        if (_update?.Manifest is not { } manifest || _installingUpdate) return;
        if (UpdateService.InstalledFolder() is not { } folder)
        {
            Open(manifest.NotesUrl ?? UpdateService.ReleasesPage ?? manifest.Setup.Url); // portable copy: download by hand
            return;
        }
        _installingUpdate = true;
        try
        {
            _main.ShowUpdateProgress("Downloading the update…");
            var progress = new Progress<double>(p => _main.ShowUpdateProgress($"Downloading the update… {p * 100:0}%"));
            var setup = await UpdateService.DownloadSetupAsync(manifest, progress);
            _main.ShowUpdateProgress("Installing the update. Download Speed Manager will restart in a moment…");
            SaveResumeList();
            UpdateService.RunSetup(setup, folder);
            Exit();
        }
        catch (Exception ex)
        {
            _installingUpdate = false;
            _main.ShowUpdateError($"The update couldn't be installed: {ex.Message}");
        }
    }

    public void OpenReleaseNotes()
    {
        if ((_update?.Manifest?.NotesUrl ?? UpdateService.ReleasesPage) is { } url) Open(url);
    }

    /// <summary>Remember what was downloading so the updated app continues it.</summary>
    private void SaveResumeList()
    {
        var ids = Queue.Items.Where(d => d.IsRunning || d.Status is DownloadStatus.Queued).Select(d => d.Id).ToList();
        try { Json.WriteAtomic(ResumeFile, ids); }
        catch { /* the downloads can still be resumed by hand */ }
    }

    /// <summary>Called once at startup: continue downloads that were running when an update restarted the app.</summary>
    public void ResumeAfterUpdate()
    {
        if (!File.Exists(ResumeFile)) return;
        var recent = DateTime.Now - File.GetLastWriteTime(ResumeFile) < TimeSpan.FromHours(1);
        var ids = Json.Read<List<string>>(ResumeFile) ?? [];
        try { File.Delete(ResumeFile); } catch { /* ignored */ }
        if (!recent) return;
        foreach (var id in ids)
        {
            if (Queue.Find(id) is { } item) Queue.Resume(item);
        }
    }

    private static void Inform(string heading, string text) =>
        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = AppPaths.AppName,
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Information,
        });

    public AppSettings Settings { get; }
    public DownloadQueue Queue { get; }
    public Icon AppIcon { get; }
    public bool IsExiting { get; private set; }

    /// <summary>Run on the UI thread.</summary>
    public void Post(Action action)
    {
        if (!_main.IsDisposed && _main.IsHandleCreated) _main.BeginInvoke(action);
    }

    public void ShowMain()
    {
        _main.Show();
        if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
        _main.Activate();
        Native.SetForegroundWindow(_main.Handle);
    }

    public void HandleCommand(AppCommand command)
    {
        if (command.Type == "exit")
        {
            Exit(); // pauses downloads (they resume later) and saves the list
            return;
        }
        if (command.StartQueue) Queue.ResumeAll();
        if (command.Url is { Length: > 0 } url)
        {
            var request = new AddRequest(url, command.FileName, command.Folder, null, command.Headers(), command.StartNow);
            if (command.Silent) Add(request, quiet: true);
            else ShowAddDialog(request);
        }
        else if (!command.Tray && !command.StartQueue)
        {
            ShowMain();
        }
    }

    public void ShowAddDialog(string? url = null) => ShowAddDialog(new AddRequest(url ?? ""));

    public void ShowAddDialog(AddRequest prefill)
    {
        var form = new AddDownloadForm(Settings, prefill, AppIcon);
        form.FormClosed += (_, _) =>
        {
            if (form.DialogResult == DialogResult.OK && form.Result is { } request) Add(request, quiet: false);
        };
        form.Show();
    }

    public DownloadItem? Add(AddRequest request, bool quiet)
    {
        try
        {
            var item = Queue.Add(request);
            if (quiet) _quiet.Add(item.Id);
            return item;
        }
        catch (Exception ex)
        {
            ShowError("Couldn't add the download", ex.Message);
            return null;
        }
    }

    public void ShowProgress(DownloadItem item)
    {
        if (!_progress.TryGetValue(item.Id, out var form) || form.IsDisposed)
        {
            var created = new ProgressForm(this, item);
            created.FormClosed += (_, _) =>
            {
                if (_progress.TryGetValue(item.Id, out var current) && current == created) _progress.Remove(item.Id);
            };
            _progress[item.Id] = form = created;
        }
        form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    public void CloseProgress(DownloadItem item)
    {
        if (_progress.Remove(item.Id, out var form)) form.Close();
    }

    public void ShowOptions(OptionsTab tab = OptionsTab.General)
    {
        using var form = new OptionsForm(this, tab);
        if (form.ShowDialog(_main.Visible ? _main : null) == DialogResult.OK) ApplySettings();
    }

    public void ApplySettings()
    {
        try { Settings.Save(); }
        catch (Exception ex) { ShowError("Couldn't save settings", ex.Message); }
        Queue.ApplySettings();
        _clipboard.Enabled = Settings.MonitorClipboard;
        if (Settings.CheckForUpdates) _updateTimer.Start();
        else _updateTimer.Stop();
        try
        {
            Startup.Apply(Settings.LaunchOnStartup);
            if (Settings.BrowserIntegration) BrowserIntegration.Register();
            else BrowserIntegration.Unregister();
        }
        catch (Exception ex)
        {
            ShowError("Couldn't update the Windows integration", ex.Message);
        }
    }

    public void SetGlobalSpeedLimit(long bytesPerSecond)
    {
        Settings.GlobalSpeedLimit = bytesPerSecond;
        ApplySettings();
    }

    public void CopyToClipboard(string text)
    {
        _clipboard.Ignore(text);
        Clipboard.SetText(text);
    }

    public void NotifyHidden()
    {
        if (_hiddenNoticeShown) return;
        _hiddenNoticeShown = true;
        _tray.ShowBalloonTip(3000, "Download Speed Manager is still running",
            "Downloads continue in the background. Right-click the icon to exit.", ToolTipIcon.Info);
    }

    public async void Exit()
    {
        if (IsExiting) return;
        IsExiting = true;
        _trayTimer.Stop();
        _updateTimer.Stop();
        foreach (var form in _progress.Values.ToList()) form.Close();
        _main.Hide();
        await Queue.ShutdownAsync(); // pause running downloads so they resume next time
        _tray.Visible = false;
        _main.Close();
        ExitThread();
    }

    /// <summary>The multi-size icon embedded from Assets\app.ico (drawn at runtime if missing).</summary>
    private static Icon LoadIcon()
    {
        using var stream = typeof(Shell).Assembly.GetManifestResourceStream("app.ico");
        return stream is null ? Glyphs.LogoIcon(32) : new Icon(stream);
    }

    public static void ShowError(string heading, string text) =>
        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = "Download Speed Manager",
            Heading = heading,
            Text = text,
            Icon = TaskDialogIcon.Error,
        });

    public static string? AsHttpUrl(string? text)
    {
        text = text?.Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }

    public static string FileNameFromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? Http.FileNameFromUrl(uri) : "";

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Couldn't open", ex.Message); }
    }

    public static void Reveal(DownloadItem item)
    {
        if (item.TargetPath is { } path && File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(item.Folder)) Open(item.Folder);
    }

    private void OnItemChanged(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Connecting && Settings.ShowProgressWindow
            && !_quiet.Contains(item.Id) && !_progress.ContainsKey(item.Id))
        {
            ShowProgress(item);
        }
    }

    private void OnItemCompleted(DownloadItem item)
    {
        if (_progress.TryGetValue(item.Id, out var form) && form.CloseWhenDone) CloseProgress(item);
        if (_quiet.Remove(item.Id)) return;
        if (Settings.ShowCompletionDialog) new CompletedForm(this, item).Show();
        else _tray.ShowBalloonTip(4000, "Download complete", item.DisplayName, ToolTipIcon.Info);
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip { Renderer = Theme.Renderer };
        var open = new ToolStripMenuItem("Open Download Speed Manager", null, (_, _) => ShowMain());
        open.Font = new Font(open.Font, FontStyle.Bold);
        var speed = new ToolStripMenuItem("Speed limit", Glyphs.Render(Glyphs.Speed, 16));
        speed.DropDownOpening += (_, _) => Dsm.Ui.MainForm.FillSpeedMenu(speed.DropDownItems, this);
        Dsm.Ui.MainForm.FillSpeedMenu(speed.DropDownItems, this);
        menu.Items.AddRange(
        [
            open,
            new ToolStripMenuItem("Add URL…", Glyphs.Render(Glyphs.Add, 16), (_, _) => ShowAddDialog()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Resume all", Glyphs.Render(Glyphs.Play, 16, Theme.Success), (_, _) => Queue.ResumeAll()),
            new ToolStripMenuItem("Pause all", Glyphs.Render(Glyphs.Pause, 16, Theme.Warning), async (_, _) => await Queue.PauseAllAsync()),
            speed,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit()),
        ]);
        _tray.Icon = new Icon(AppIcon, SystemInformation.SmallIconSize);
        _tray.Text = AppPaths.AppName;
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMain();
        };
        _tray.BalloonTipClicked += (_, _) => ShowMain();
    }

    private void UpdateTray()
    {
        var items = Queue.Items;
        var active = items.Count(d => d.IsActive);
        var speed = items.Where(d => d.Status == DownloadStatus.Downloading).Sum(d => d.Speed);
        var text = active > 0 ? $"{AppPaths.AppName}\n{active} active, {Format.Speed(speed)}" : AppPaths.AppName;
        if (_tray.Text != text) _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _trayTimer.Dispose();
            _updateTimer.Dispose();
            _clipboard.Dispose();
        }
        base.Dispose(disposing);
    }
}
