using Dsm.Integration;
using Dsm.Updates;

namespace Dsm.Ui;

internal enum OptionsTab { General, Connection, Folders, Browser, Updates }

/// <summary>Options: a navigation pane on the left and one page per section.</summary>
internal sealed class OptionsForm : Form
{
    private static readonly string[] Themes = ["Dark", "Light", "System"];
    private readonly AppSettings _settings;
    private readonly CheckBox _startup = Check("Start Download Speed Manager when Windows starts");
    private readonly CheckBox _clipboard = Check("Offer to download links copied to the clipboard");
    private readonly CheckBox _progress = Check("Show a progress window for each download");
    private readonly CheckBox _completed = Check("Show a dialog when a download finishes");
    private readonly CheckBox _tray = Check("Keep running in the notification area when the window is closed");
    private readonly ComboBox _theme = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly NumericUpDown _connections = Number(1, 32);
    private readonly NumericUpDown _concurrent = Number(1, 16);
    private readonly NumericUpDown _limit = Number(0, 1_000_000);
    private readonly CheckBox _ipv4 = Check("Prefer IPv4 connections (falls back to IPv6 automatically)");
    private readonly TextBox _folder = new();
    private readonly CheckBox _byCategory = Check("Save files into category folders (Video, Audio, Documents, …)");
    private readonly CheckBox _browser = Check("Catch downloads from Chrome, Edge and Firefox");
    private readonly CheckBox _checkUpdates = Check("Check for updates automatically");
    private readonly CheckBox _autoInstall = Check("Install updates without asking when no downloads are running");
    private static readonly (string Text, int Hours)[] Intervals = [("Every hour", 1), ("Every 6 hours", 6), ("Every day", 24)];
    private readonly ComboBox _interval = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly Label _updateStatus = Note("");
    private readonly FlowLayoutPanel _browserStatus = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
    private readonly Panel _pages = new() { Dock = DockStyle.Fill, Padding = new Padding(20, 14, 20, 6) };
    private readonly List<(RadioButton Nav, Control Page)> _sections = [];

    public OptionsForm(Shell shell, OptionsTab tab)
    {
        _settings = shell.Settings;
        UiKit.Dialog(this, "Options");
        Icon = shell.AppIcon;
        AutoSize = false; // the page area fills the window, so it can't size itself
        ClientSize = new Size(LogicalToDeviceUnits(700), LogicalToDeviceUnits(400));

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = LogicalToDeviceUnits(172),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Sidebar,
            Padding = new Padding(8, 12, 8, 8),
        };
        _theme.Items.AddRange(Themes);
        _folder.Width = LogicalToDeviceUnits(330);
        _folder.Margin = new Padding(0, 4, 0, 0); // centred on the taller button
        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        folderRow.Controls.AddRange([_folder, UiKit.Button("Browse…", (_, _) => Browse())]);

        AddSection(nav, "General", Glyphs.Settings, Page(_startup, _clipboard, _progress, _completed, _tray,
            Row("Theme", _theme), Note("A new theme is used the next time the app starts.")));
        AddSection(nav, "Connection", Glyphs.Speed, Page(
            Row("Connections per download", _connections),
            Row("Downloads at the same time", _concurrent),
            Row("Overall speed limit (KB/s, 0 = no limit)", _limit),
            _ipv4,
            Note("More connections help on slow or distant servers. Some servers refuse extra connections; the app then uses fewer automatically.")));
        AddSection(nav, "Save to", Glyphs.Folder, Page(UiKit.Caption("Download folder"), folderRow, _byCategory));
        AddSection(nav, "Browser", Glyphs.Link, Page(_browser, _browserStatus,
            Note("1. Open chrome://extensions (or edge://extensions) and turn on Developer mode.\n" +
                 "2. Choose \"Load unpacked\" and select the extension folder.\n" +
                 "3. Downloads you start in the browser now open here."),
            UiKit.Button("Open extension folder", (_, _) => OpenExtensionFolder())));
        _interval.Items.AddRange([.. Intervals.Select(i => i.Text)]);
        var checkNow = UiKit.Button("Check now", async (_, _) =>
        {
            await shell.CheckForUpdatesNowAsync();
            _updateStatus.Text = UpdateStatus(shell);
        });
        var intervalRow = Row("How often to check", _interval);
        AddSection(nav, "Updates", Glyphs.Download, Page(_checkUpdates, intervalRow, _autoInstall,
            _updateStatus, checkNow, Note(UpdateSource())));
        void UpdateEnabled()
        {
            _autoInstall.Enabled = _interval.Enabled = _checkUpdates.Checked;
            intervalRow.Controls[0].ForeColor = _checkUpdates.Checked ? Theme.Text : Theme.Muted; // the caption dims with its field
        }
        _checkUpdates.CheckedChanged += (_, _) => UpdateEnabled();
        Select((int)tab);

        _startup.Checked = _settings.LaunchOnStartup;
        _clipboard.Checked = _settings.MonitorClipboard;
        _progress.Checked = _settings.ShowProgressWindow;
        _completed.Checked = _settings.ShowCompletionDialog;
        _tray.Checked = _settings.CloseToTray;
        _theme.SelectedItem = Themes.Contains(_settings.Theme) ? _settings.Theme : "Dark";
        _connections.Value = Math.Clamp(_settings.DefaultConnections, 1, 32);
        _concurrent.Value = Math.Clamp(_settings.MaxConcurrent, 1, 16);
        _limit.Value = Math.Clamp(_settings.GlobalSpeedLimit / 1024, 0, 1_000_000);
        _ipv4.Checked = _settings.PreferIPv4;
        _folder.Text = _settings.DownloadFolder;
        _byCategory.Checked = _settings.SortByCategory;
        _browser.Checked = _settings.BrowserIntegration;
        _checkUpdates.Checked = _settings.CheckForUpdates;
        _autoInstall.Checked = _settings.AutoInstallUpdates;
        UpdateEnabled();
        _interval.SelectedIndex = Math.Max(0, Array.FindIndex(Intervals, i => i.Hours >= _settings.UpdateCheckHours));
        _updateStatus.Text = UpdateStatus(shell);
        foreach (var (browser, registered) in BrowserIntegration.Status())
        {
            _browserStatus.Controls.Add(new Label
            {
                Text = $"● {browser}: {(registered ? "connected" : "not connected")}",
                AutoSize = true,
                ForeColor = registered ? Theme.SuccessText : Theme.DangerText, // green works, red needs attention
                Margin = new Padding(0, 4, 18, 4),
            });
        }

        var ok = UiKit.Button("OK", (_, _) => Save());
        var cancel = UiKit.Button("Cancel", (_, _) => Close());
        // Docking runs from the last control added: the navigation pane takes the full height,
        // and the buttons sit under the page.
        Controls.Add(_pages);
        Controls.Add(UiKit.ButtonRow(cancel, ok));
        Controls.Add(nav);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void AddSection(FlowLayoutPanel nav, string title, char glyph, Control page)
    {
        var button = new RadioButton
        {
            Text = title,
            Appearance = Appearance.Button,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            Image = Glyphs.Render(glyph, LogicalToDeviceUnits(16)),
            Size = new Size(LogicalToDeviceUnits(154), LogicalToDeviceUnits(34)),
            Margin = new Padding(0, 0, 0, 2),
            Padding = new Padding(8, 0, 0, 0),
            ForeColor = Theme.Text,
            BackColor = Theme.Sidebar,
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.CheckedBackColor = Theme.Selected;
        button.FlatAppearance.MouseOverBackColor = Theme.NavHover;
        button.FlatAppearance.MouseDownBackColor = Theme.Selected;
        // The selected section looks like the main window's sidebar: accent marker and accent icon.
        button.Paint += (_, e) =>
        {
            if (!button.Checked) return;
            using var marker = new SolidBrush(Theme.Accent);
            var height = button.Height / 2;
            e.Graphics.FillRectangle(marker, 0, (button.Height - height) / 2, LogicalToDeviceUnits(3), height);
        };
        var index = _sections.Count;
        button.CheckedChanged += (_, _) =>
        {
            button.Image = Glyphs.Render(glyph, LogicalToDeviceUnits(16), button.Checked ? Theme.Accent : null);
            if (button.Checked) Select(index);
        };
        page.Visible = false;
        _pages.Controls.Add(page);
        nav.Controls.Add(button);
        _sections.Add((button, page));
    }

    private void Select(int index)
    {
        for (var i = 0; i < _sections.Count; i++)
        {
            var (navButton, page) = _sections[i];
            page.Visible = i == index;
            if (i == index && !navButton.Checked) navButton.Checked = true;
        }
    }

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 4) };

    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, Width = 90 };

    private static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(470, 0),
        ForeColor = Theme.Muted,
        Margin = new Padding(0, 10, 0, 6),
    };

    private static FlowLayoutPanel Row(string caption, Control field)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        var label = UiKit.Caption(caption);
        label.MinimumSize = new Size(260, 0);
        row.Controls.AddRange([label, field]);
        return row;
    }

    private static FlowLayoutPanel Page(params Control[] controls)
    {
        var page = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Window,
        };
        page.Controls.AddRange(controls);
        return page;
    }

    /// <summary>"Version 1.2.0 is available." or "Up to date. Last checked 19-11-2026 10:42."</summary>
    private static string UpdateStatus(Shell shell)
    {
        if (shell.LastCheck is { State: UpdateState.Available, Manifest: { } manifest })
            return $"Version {manifest.Version} is available. Use Install update in the bar at the top of the window.";
        if (shell.LastCheck is { State: UpdateState.Failed } failed)
            return $"The last check didn't work: {failed.Error}";
        return shell.Settings.LastUpdateCheck is { } when
            ? $"Up to date. Last checked {when:g}."
            : "Not checked yet.";
    }

    private static string UpdateSource()
    {
        var version = $"You have version {UpdateService.CurrentVersion.ToString(3)}.";
        if (UpdateService.Repository is not { } repository)
            return version + " This copy was built locally, so it has no update source.";
        var portable = UpdateService.InstalledFolder() is null
            ? " This copy isn't installed, so updates are offered as a download instead of installed."
            : "";
        return $"{version} Updates come from the GitHub releases of {repository} and are installed only if they carry the project's signature.{portable}";
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text, UseDescriptionForTitle = true, Description = "Download folder" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
    }

    private static void OpenExtensionFolder()
    {
        if (BrowserIntegration.ExtensionFolder() is { } folder) Shell.Open(folder);
        else MessageBox.Show("The extension folder was not found next to the app.", "Download Speed Manager");
    }

    private void Save()
    {
        _settings.LaunchOnStartup = _startup.Checked;
        _settings.MonitorClipboard = _clipboard.Checked;
        _settings.ShowProgressWindow = _progress.Checked;
        _settings.ShowCompletionDialog = _completed.Checked;
        _settings.CloseToTray = _tray.Checked;
        _settings.Theme = (string?)_theme.SelectedItem ?? "Dark";
        _settings.DefaultConnections = (int)_connections.Value;
        _settings.MaxConcurrent = (int)_concurrent.Value;
        _settings.GlobalSpeedLimit = (long)_limit.Value * 1024;
        _settings.PreferIPv4 = _ipv4.Checked;
        if (_folder.Text.Trim().Length > 0) _settings.DownloadFolder = _folder.Text.Trim();
        _settings.SortByCategory = _byCategory.Checked;
        _settings.BrowserIntegration = _browser.Checked;
        _settings.CheckForUpdates = _checkUpdates.Checked;
        _settings.AutoInstallUpdates = _autoInstall.Checked;
        _settings.UpdateCheckHours = Intervals[Math.Max(0, _interval.SelectedIndex)].Hours;
        DialogResult = DialogResult.OK;
        Close();
    }
}
