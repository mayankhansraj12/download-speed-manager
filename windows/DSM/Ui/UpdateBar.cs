namespace Dsm.Ui;

/// <summary>Bar under the toolbar: "Version X is available" with Install update / What's new / Later.</summary>
internal sealed class UpdateBar : Panel
{
    private readonly Label _text = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(8, 7, 0, 0) };
    private readonly PictureBox _icon = new() { SizeMode = PictureBoxSizeMode.CenterImage, Margin = new Padding(0, 3, 0, 0) };
    private readonly Button _install;
    private readonly Button _notes;
    private readonly Button _later;

    public UpdateBar()
    {
        Dock = DockStyle.Top;
        Visible = false;
        BackColor = Theme.Chrome;
        Padding = new Padding(14, 7, 12, 7);
        Height = LogicalToDeviceUnits(46);
        _icon.Size = new Size(LogicalToDeviceUnits(22), LogicalToDeviceUnits(22));

        _install = UiKit.Button("Install update", (_, _) => InstallClicked?.Invoke(this, EventArgs.Empty));
        Theme.Tint(_install, ButtonRole.Go);
        _notes = UiKit.Button("What's new", (_, _) => NotesClicked?.Invoke(this, EventArgs.Empty));
        _later = UiKit.Button("Later", (_, _) => Visible = false);
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            BackColor = Theme.Chrome,
        };
        buttons.Controls.AddRange([_later, _notes, _install]);
        var message = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Theme.Chrome };
        message.Controls.AddRange([_icon, _text]);
        Controls.Add(message);
        Controls.Add(buttons);
    }

    public event EventHandler? InstallClicked;
    public event EventHandler? NotesClicked;

    public void ShowAvailable(string version, string current, bool required, bool portable)
    {
        _icon.Image = Glyphs.Render(Glyphs.Download, LogicalToDeviceUnits(20), required ? Theme.Warning : Theme.Success);
        _text.ForeColor = Theme.Text;
        _text.Text = required
            ? $"Important update: version {version} fixes problems in version {current}."
            : $"Version {version} is available. You have {current}.";
        _install.Text = portable ? "Download" : "Install update";
        _install.Enabled = _notes.Enabled = _later.Enabled = true;
        _later.Visible = true;
        Visible = true;
    }

    public void ShowProgress(string text)
    {
        _text.ForeColor = Theme.Text;
        _text.Text = text;
        _install.Enabled = _later.Enabled = false;
        Visible = true;
    }

    public void ShowError(string text)
    {
        _icon.Image = Glyphs.Render(Glyphs.Info, LogicalToDeviceUnits(20), Theme.Danger);
        _text.ForeColor = Theme.DangerText;
        _text.Text = text;
        _install.Text = "Try again";
        _install.Enabled = _later.Enabled = true;
        Visible = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var accent = new SolidBrush(Theme.Success);
        e.Graphics.FillRectangle(accent, 0, 0, LogicalToDeviceUnits(3), Height);
        using var line = new Pen(Theme.Border);
        e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
    }
}
