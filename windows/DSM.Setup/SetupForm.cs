namespace Dsm.Setup;

/// <summary>Install or uninstall wizard: step list on the left, one page on the right, buttons below.</summary>
internal sealed partial class SetupForm : Form
{
    private readonly SetupOptions _options;
    private readonly bool _uninstall;
    private readonly Installer.ExistingInstall? _existing;
    private readonly string[] _steps;
    private readonly List<(PictureBox Marker, Label Name)> _stepRows = [];
    private readonly FlowLayoutPanel _page = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        BackColor = Look.Window,
        Padding = Look.Px(32, 28, 16, 8),
    };
    private readonly FlatButton _back;
    private readonly FlatButton _next;
    private readonly FlatButton _cancel;
    private readonly TextBox _folder = new() { AccessibleName = "Install to" };
    private readonly Field _folderField;
    private readonly FlowLayoutPanel _folderRow = new() { AutoSize = true, WrapContents = false, Margin = Look.Px(0, 0, 0, 8) };
    private readonly Label _folderError = Look.Paragraph("", Look.Danger);
    private readonly CheckBox _desktop = Check("Create a &desktop shortcut", true);
    private readonly CheckBox _launch = Check("&Start Download Speed Manager now", true);
    private readonly CheckBox _removeData = Check("Also &delete my settings and download list", false);
    private readonly Meter _meter = new();
    private readonly Label _status = Look.Paragraph("", Look.Muted);
    private int _step;
    private bool _busy;
    private string? _error;
    private string _doneFolder = "";

    public SetupForm(SetupOptions options)
    {
        _options = options;
        _uninstall = options.Uninstall;
        // Screenshots for the docs show a clean machine, never this computer's installs or paths.
        _existing = options.CaptureDir is null ? Installer.FindExisting() : null;
        _steps = _uninstall ? ["Confirm", "Remove", "Finish"] : ["Welcome", "Options", "Install", "Finish"];

        Text = _uninstall ? "Uninstall Download Speed Manager" : "Download Speed Manager Setup";
        Icon = Look.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Look.Window;
        ForeColor = Look.Text;
        ClientSize = Look.Px(720, 440);

        // Folder box and Browse button: one row, the same height, as wide as the text column.
        var browse = Look.Button("B&rowse…", Look.NeutralFill, (_, _) => Browse());
        _folderField = new Field(_folder) { Margin = Padding.Empty };
        _folderField.Width = Look.ContentWidth - browse.Margin.Left - Math.Max(browse.MinimumSize.Width, browse.PreferredSize.Width);
        _folderRow.Controls.AddRange([_folderField, browse]);
        _folder.Text = options.Folder ?? _existing?.Folder ?? Installer.DefaultFolder;
        _folder.TextChanged += (_, _) => ShowFolderError(null);
        _folderError.Visible = false;
        _folderError.Margin = Look.Px(0, 0, 0, 8);
        // Updating: keep the user's earlier choice instead of re-adding a shortcut they deleted.
        _desktop.Checked = options.DesktopShortcut ?? (_existing is null || Installer.HasDesktopShortcut);
        _desktop.Margin = Look.Px(0, 6, 0, 10);
        _meter.Size = new Size(Look.ContentWidth, Look.Px(6));
        _meter.Margin = Look.Px(0, 8, 0, 12);

        _back = Look.Button("&Back", Look.NeutralFill, (_, _) => ShowStep(_step - 1));
        _next = Look.Button("&Next", Look.AccentFill, (_, _) => Next());
        _cancel = Look.Button("Cancel", Look.NeutralFill, (_, _) => Close());
        // Tab order follows the buttons as you see them, left to right.
        _back.TabIndex = 0;
        _next.TabIndex = 1;
        _cancel.TabIndex = 2;
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            BackColor = Look.Window,
            // Right-to-left flow adds the left padding on the right as well, so it all goes on the right:
            // 38 = 720 - 210 (sidebar) - 32 (page gutter) - 440 (text column) lines the buttons up with the text.
            Padding = Look.Px(0, 14, 38, 14),
        };
        footer.Controls.AddRange([_cancel, _next, _back]);
        footer.Paint += (_, e) =>
        {
            using var line = new Pen(Look.Border);
            e.Graphics.DrawLine(line, 0, 0, footer.Width, 0);
        };

        // Docking runs from the last control added: footer first, then sidebar, then the page fills the rest.
        Controls.Add(_page);
        Controls.Add(BuildSidebar());
        Controls.Add(footer);
        AcceptButton = _next;
        HandleCreated += (_, _) => Look.DarkTitleBar(this);
        ShowStep(0);
    }

    private static CheckBox Check(string text, bool on) => new()
    {
        Text = text,
        Checked = on,
        AutoSize = true,
        ForeColor = Look.Text,
        Margin = Look.Px(0, 2, 0, 10),
    };

    private Control BuildSidebar()
    {
        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = Look.Px(210),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Look.Sidebar,
            Padding = Look.Px(24, 28, 16, 16),
        };
        var logo = Look.Px(56);
        sidebar.Controls.Add(new PictureBox
        {
            Image = Look.Logo(logo),
            Size = new Size(logo, logo),
            Margin = Look.Px(0, 0, 0, 14),
            AccessibleRole = AccessibleRole.Graphic,
            AccessibleName = Installer.AppName,
        });
        sidebar.Controls.Add(new Label
        {
            Text = Installer.AppName,
            AutoSize = true,
            MaximumSize = new Size(sidebar.Width - sidebar.Padding.Horizontal, 0),
            UseMnemonic = false,
            ForeColor = Look.Text,
            Font = Look.BrandFont,
            Margin = Padding.Empty,
        });
        sidebar.Controls.Add(new Label { Text = "Version " + Installer.Version, AutoSize = true, ForeColor = Look.Muted, Margin = Look.Px(0, 2, 0, 24) });

        // One row per step; the marker and the name are centred on the row.
        var steps = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, BackColor = Look.Sidebar, Margin = Padding.Empty };
        steps.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        steps.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var marker = Look.Px(16);
        foreach (var name in _steps)
        {
            steps.RowStyles.Add(new RowStyle(SizeType.Absolute, Look.Px(30)));
            var icon = new PictureBox { Size = new Size(marker, marker), Anchor = AnchorStyles.Left, Margin = Look.Px(0, 0, 10, 0), AccessibleRole = AccessibleRole.Graphic };
            var label = new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty, UseMnemonic = false };
            steps.Controls.Add(icon);
            steps.Controls.Add(label);
            _stepRows.Add((icon, label));
        }
        sidebar.Controls.Add(steps);
        return sidebar;
    }

    /// <summary>Finished steps get a green check, the current one a blue ring (a red cross if it failed), the rest a grey ring.</summary>
    private void RenderSteps()
    {
        var last = _steps.Length - 1;
        var size = Look.Px(16);
        for (var i = 0; i < _steps.Length; i++)
        {
            var (marker, label) = _stepRows[i];
            var failed = i == last && _step == last && _error is not null;
            var done = !failed && (i < _step || (i == last && _step == last));
            var old = marker.Image;
            marker.Image = failed ? Look.StatusIcon(Tone.Danger, size)
                : done ? Look.StatusIcon(Tone.Success, size)
                : Look.Ring(size, i == _step ? Look.Accent : Look.Muted, current: i == _step);
            marker.AccessibleName = failed ? "Failed" : done ? "Done" : i == _step ? "Current step" : "Not started";
            old?.Dispose();
            label.ForeColor = i <= _step ? Look.Text : Look.Muted;
            label.Font = i == _step ? Look.StrongFont : Font;
        }
    }

    private void ShowStep(int step)
    {
        _step = step;
        _page.SuspendLayout();
        _page.Controls.Clear();
        if (_uninstall) BuildUninstallPage(step);
        else BuildInstallPage(step);
        _page.ResumeLayout();
        RenderSteps();
        UpdateButtons();
        // Enter and Space act on the page's main button, never on Cancel or on a control that has just gone.
        if (_next.Enabled) ActiveControl = _next;
    }

    private void Add(Control control)
    {
        control.TabIndex = _page.Controls.Count; // tab order runs down the page
        _page.Controls.Add(control);
    }

    private static PictureBox Badge(Tone tone)
    {
        var size = Look.Px(48);
        return new PictureBox
        {
            Image = Look.StatusIcon(tone, size),
            Size = new Size(size, size),
            Margin = Look.Px(0, 0, 0, 16), // top in line with the logo in the sidebar
            AccessibleRole = AccessibleRole.Graphic,
            AccessibleName = tone == Tone.Success ? "Done" : "Failed",
        };
    }

    private void BuildInstallPage(int step)
    {
        switch (step)
        {
            case 0:
                Add(Look.Heading("Install Download Speed Manager"));
                Add(Look.Paragraph("Fast multi-connection downloads with pause and resume, speed limits, a download queue and browser capture."));
                Add(Look.Paragraph("Setup installs the app for your Windows account only, so no administrator permission is needed.", Look.Muted));
                if (_existing is not null)
                    Add(new Notice(Tone.Info, $"Version {_existing.Version ?? "?"} is already installed. Setup will update it and keep your settings and unfinished downloads."));
                if (_options.CaptureDir is null && Installer.RunningElsewhere(_folder.Text) is { } other)
                    Add(new Notice(Tone.Warning, $"Download Speed Manager is running from {other}. Exit it first (right-click its notification-area icon → Exit), otherwise the new copy will open that one."));
                if (!Installer.HasPayload)
                    Add(new Notice(Tone.Danger, "This setup file is incomplete: the app files are missing. Download the setup again."));
                Add(Look.Paragraph("Free software under the MIT License.", Look.Muted));
                break;
            case 1:
                Add(Look.Heading("Choose options"));
                var caption = Look.Paragraph("Install &to", Look.Muted);
                caption.UseMnemonic = true; // Alt+T moves to the folder box
                caption.Margin = Look.Px(0, 0, 0, 6);
                Add(caption);
                Add(_folderRow);
                Add(_folderError);
                Add(_desktop);
                Add(Look.Paragraph("A Start menu shortcut is always added. You can uninstall any time from Settings → Apps.", Look.Muted));
                break;
            case 2:
                Add(Look.Heading(_existing is null ? "Installing…" : "Updating…"));
                Add(_meter);
                Add(_status);
                break;
            default:
                if (_error is null)
                {
                    Add(Badge(Tone.Success));
                    Add(Look.Heading("Download Speed Manager is installed"));
                    Add(Look.Paragraph($"Find it in the Start menu. Installed to {_doneFolder}."));
                    Add(Look.Paragraph("Tip: to catch downloads from your browser, open Options → Browser in the app.", Look.Muted));
                    Add(_launch);
                }
                else
                {
                    Add(Badge(Tone.Danger));
                    Add(Look.Heading("Setup couldn't finish"));
                    Add(new Notice(Tone.Danger, _error));
                    Add(Look.Paragraph("Close Download Speed Manager if it is open, then run setup again.", Look.Muted));
                }
                break;
        }
    }

    private void BuildUninstallPage(int step)
    {
        var folder = _options.Folder ?? _existing?.Folder;
        switch (step)
        {
            case 0:
                Add(Look.Heading("Remove Download Speed Manager?"));
                Add(folder is null
                    ? new Notice(Tone.Warning, "Download Speed Manager doesn't seem to be installed.")
                    : Look.Paragraph($"This removes the app from {folder}, its shortcuts, and its browser connection."));
                Add(new Notice(Tone.Success, "Files you downloaded are never deleted."));
                Add(_removeData);
                break;
            case 1:
                Add(Look.Heading("Removing…"));
                Add(_meter);
                Add(_status);
                break;
            default:
                if (_error is null)
                {
                    Add(Badge(Tone.Success));
                    Add(Look.Heading("Download Speed Manager was removed"));
                    Add(Look.Paragraph("Your downloaded files are still where you saved them.", Look.Muted));
                }
                else
                {
                    Add(Badge(Tone.Danger));
                    Add(Look.Heading("Uninstall couldn't finish"));
                    Add(new Notice(Tone.Danger, _error));
                }
                break;
        }
    }

    private void UpdateButtons()
    {
        var last = _step == _steps.Length - 1;
        _back.Visible = !_uninstall && _step == 1;
        _cancel.Visible = !last;
        _cancel.Enabled = !_busy;
        // While working, the button keeps the name of what is running (Install, Update, Uninstall), greyed out.
        var (text, fill) = _uninstall
            ? (last ? ("&Close", Look.AccentFill) : ("&Uninstall", Look.StopFill))
            : _step switch
            {
                0 => ("&Next", Look.AccentFill),
                1 or 2 => (_existing is null ? "&Install" : "&Update", Look.GoFill),
                _ => (_error is null ? "&Finish" : "&Close", _error is null ? Look.GoFill : Look.NeutralFill),
            };
        _next.Text = text;
        _next.Fill = fill;
        var canStart = _uninstall ? _options.Folder is not null || _existing is not null : Installer.HasPayload;
        _next.Enabled = !_busy && (last || canStart);
    }

    private void Next()
    {
        if (_step == _steps.Length - 1)
        {
            if (!_uninstall && _error is null && _launch.Checked) TryLaunch();
            Close();
            return;
        }
        if (!_uninstall && _step == 1 && !ValidFolder()) return;
        if (_uninstall || _step == 1)
        {
            ShowStep(_step + 1);
            _ = RunAsync();
        }
        else
        {
            ShowStep(_step + 1);
        }
    }

    private bool ValidFolder()
    {
        var folder = _folder.Text.Trim();
        var ok = folder.Length > 0 && Path.IsPathFullyQualified(folder) && folder.IndexOfAny(Path.GetInvalidPathChars()) < 0;
        if (!ok)
        {
            ShowFolderError("Choose a full folder path, for example C:\\Users\\you\\Apps\\Download Speed Manager.");
            _folder.Focus();
            _folder.SelectAll();
        }
        return ok;
    }

    /// <summary>The message under the folder box; null hides it (as soon as the folder is edited).</summary>
    private void ShowFolderError(string? message)
    {
        _folderError.Text = message ?? "";
        _folderError.Visible = message is not null;
        _folderField.Invalid = message is not null;
    }

    private async Task RunAsync()
    {
        _busy = true;
        UpdateButtons();
        _meter.Fill = Look.Success;
        var progress = new Progress<(int Percent, string Message)>(p =>
        {
            _meter.Value = p.Percent / 100.0;
            _status.Text = p.Message;
        });
        var folder = _uninstall ? _options.Folder ?? _existing?.Folder ?? "" : _folder.Text.Trim();
        var desktop = _desktop.Checked;
        var removeData = _removeData.Checked;
        try
        {
            if (_uninstall) await Task.Run(() => Installer.Uninstall(folder, removeData, progress));
            else await Task.Run(() => Installer.Install(folder, desktop, progress));
            _doneFolder = Path.GetFullPath(folder);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Program.Log(ex);
        }
        _busy = false;
        ShowStep(_steps.Length - 1);
    }

    private void TryLaunch()
    {
        try { Installer.Launch(_doneFolder); }
        catch (Exception ex) { Program.Log(ex); }
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text, UseDescriptionForTitle = true, Description = "Install Download Speed Manager to" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            // Keep the app in its own folder, like most Windows software.
            var chosen = dialog.SelectedPath;
            _folder.Text = Path.GetFileName(chosen).Equals(Installer.AppName, StringComparison.OrdinalIgnoreCase)
                ? chosen
                : Path.Combine(chosen, Installer.AppName);
        }
    }

    /// <summary>Esc closes the wizard like the title bar's close button does, but not while it is working.</summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (!_busy) Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy) e.Cancel = true; // never leave a half-finished install behind
        base.OnFormClosing(e);
    }
}
