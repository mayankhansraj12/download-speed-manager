namespace Dsm.Setup;

/// <summary>Install or uninstall wizard: step list on the left, one page on the right, buttons below.</summary>
internal sealed partial class SetupForm : Form
{
    private readonly SetupOptions _options;
    private readonly bool _uninstall;
    private readonly Installer.ExistingInstall? _existing;
    private readonly string[] _steps;
    private readonly FlowLayoutPanel _stepList = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Look.Sidebar };
    private readonly FlowLayoutPanel _page = new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        BackColor = Look.Window,
        Padding = new Padding(32, 28, 24, 8),
    };
    private readonly Button _back;
    private readonly Button _next;
    private readonly Button _cancel;
    private readonly TextBox _folder = new() { BorderStyle = BorderStyle.FixedSingle, BackColor = Look.Input, ForeColor = Look.Text };
    private readonly Label _folderError = Look.Paragraph("", Look.Danger);
    private readonly CheckBox _desktop = Check("Create a desktop shortcut", true);
    private readonly CheckBox _launch = Check("Start Download Speed Manager now", true);
    private readonly CheckBox _removeData = Check("Also delete my settings and download list", false);
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
        _existing = Installer.FindExisting();
        _steps = _uninstall ? ["Confirm", "Remove", "Finish"] : ["Welcome", "Options", "Install", "Finish"];

        Text = _uninstall ? "Uninstall Download Speed Manager" : "Download Speed Manager Setup";
        Icon = Look.AppIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Look.Window;
        ForeColor = Look.Text;
        ClientSize = new Size(LogicalToDeviceUnits(720), LogicalToDeviceUnits(440));

        _folder.Text = options.Folder ?? _existing?.Folder ?? Installer.DefaultFolder;
        _folder.Width = LogicalToDeviceUnits(320);
        // Updating: keep the user's earlier choice instead of re-adding a shortcut they deleted.
        _desktop.Checked = options.DesktopShortcut ?? (_existing is null || Installer.HasDesktopShortcut);
        _meter.Size = new Size(LogicalToDeviceUnits(430), LogicalToDeviceUnits(8));
        _meter.Margin = new Padding(0, 6, 0, 12);

        _back = Look.Button("Back", Look.NeutralFill, (_, _) => ShowStep(_step - 1));
        _next = Look.Button("Next", Look.Accent, (_, _) => Next());
        _cancel = Look.Button("Cancel", Look.NeutralFill, (_, _) => Close());
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            BackColor = Look.Window,
            Padding = new Padding(16, 12, 20, 16),
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
        Margin = new Padding(0, 4, 0, 8),
    };

    private Control BuildSidebar()
    {
        var sidebar = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = LogicalToDeviceUnits(210),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Look.Sidebar,
            Padding = new Padding(22, 26, 12, 12),
        };
        var logo = LogicalToDeviceUnits(56);
        sidebar.Controls.Add(new PictureBox { Image = Look.Logo(logo), Size = new Size(logo, logo), Margin = new Padding(0, 0, 0, 14) });
        sidebar.Controls.Add(new Label { Text = Installer.AppName, AutoSize = true, ForeColor = Look.Text, Font = new Font("Segoe UI Semibold", 11f), Margin = Padding.Empty });
        sidebar.Controls.Add(new Label { Text = "Version " + Installer.Version, AutoSize = true, ForeColor = Look.Muted, Margin = new Padding(0, 2, 0, 28) });
        sidebar.Controls.Add(_stepList);
        return sidebar;
    }

    /// <summary>Finished steps get a green check, the current one a blue dot (red cross if it failed).</summary>
    private void RenderSteps()
    {
        _stepList.SuspendLayout();
        _stepList.Controls.Clear();
        var last = _steps.Length - 1;
        for (var i = 0; i < _steps.Length; i++)
        {
            var finishedOk = i == last && _step == last && _error is null;
            var (glyph, color) = i < _step || finishedOk ? (Look.Check, Look.Success)
                : i == _step ? (_error is not null && i == last ? (Look.Cancel, Look.Danger) : (Look.CircleFill, Look.Accent))
                : (Look.CircleRing, Look.Muted);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6), BackColor = Look.Sidebar };
            var size = LogicalToDeviceUnits(16);
            row.Controls.Add(new PictureBox { Image = Look.Glyph(glyph, size, color), Size = new Size(size, size), Margin = new Padding(0, 3, 10, 0) });
            row.Controls.Add(new Label
            {
                Text = _steps[i],
                AutoSize = true,
                ForeColor = i <= _step ? Look.Text : Look.Muted,
                Font = new Font(Font, i == _step ? FontStyle.Bold : FontStyle.Regular),
                Margin = new Padding(0, 2, 0, 0),
            });
            _stepList.Controls.Add(row);
        }
        _stepList.ResumeLayout();
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
    }

    private void Add(Control control) => _page.Controls.Add(control);

    private PictureBox Badge(char glyph, Color color)
    {
        var size = LogicalToDeviceUnits(44);
        return new PictureBox { Image = Look.Glyph(glyph, size, color), Size = new Size(size, size), Margin = new Padding(0, 0, 0, 8) };
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
                    Add(Look.Paragraph($"Version {_existing.Version ?? "?"} is already installed. Setup will update it and keep your settings and unfinished downloads.", Look.Warning));
                if (Installer.RunningElsewhere(_folder.Text) is { } other)
                    Add(Look.Paragraph($"Download Speed Manager is running from {other}. Exit it first (right-click its notification-area icon → Exit), otherwise the new copy will open that one.", Look.Warning));
                if (!Installer.HasPayload)
                    Add(Look.Paragraph("This setup file is incomplete: the app files are missing. Download the setup again.", Look.Danger));
                Add(Look.Paragraph("Free software under the MIT License.", Look.Muted));
                break;
            case 1:
                Add(Look.Heading("Choose options"));
                Add(Look.Paragraph("Install to", Look.Muted));
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
                row.Controls.AddRange([_folder, Look.Button("Browse…", Look.NeutralFill, (_, _) => Browse())]);
                Add(row);
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
                    Add(Badge(Look.Check, Look.Success));
                    Add(Look.Heading("Download Speed Manager is installed"));
                    Add(Look.Paragraph($"Find it in the Start menu. Installed to {_doneFolder}."));
                    Add(Look.Paragraph("Tip: to catch downloads from your browser, open Options → Browser in the app.", Look.Muted));
                    Add(_launch);
                }
                else
                {
                    Add(Badge(Look.Cancel, Look.Danger));
                    Add(Look.Heading("Setup couldn't finish"));
                    Add(Look.Paragraph(_error, Look.Danger));
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
                    ? Look.Paragraph("Download Speed Manager doesn't seem to be installed.", Look.Warning)
                    : Look.Paragraph($"This removes the app from {folder}, its shortcuts, and its browser connection."));
                Add(Look.Paragraph("Files you downloaded are never deleted.", Look.Success));
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
                    Add(Badge(Look.Check, Look.Success));
                    Add(Look.Heading("Download Speed Manager was removed"));
                    Add(Look.Paragraph("Your downloaded files are still where you saved them.", Look.Muted));
                }
                else
                {
                    Add(Badge(Look.Cancel, Look.Danger));
                    Add(Look.Heading("Uninstall couldn't finish"));
                    Add(Look.Paragraph(_error, Look.Danger));
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
        var (text, fill) = _uninstall
            ? (_step == 0 ? ("Uninstall", Look.StopFill) : ("Close", Look.Accent))
            : _step switch
            {
                0 => ("Next", Look.Accent),
                1 or 2 => (_existing is null ? "Install" : "Update", Look.GoFill),
                _ => (_error is null ? "Finish" : "Close", _error is null ? Look.GoFill : Look.NeutralFill),
            };
        _next.Text = text;
        Look.Paint(_next, fill);
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
        _folderError.Text = ok ? "" : "Choose a full folder path, for example C:\\Users\\you\\Apps\\Download Speed Manager.";
        return ok;
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy) e.Cancel = true; // never leave a half-finished install behind
        base.OnFormClosing(e);
    }
}
