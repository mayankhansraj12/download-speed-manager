using Dsm.Engine;

namespace Dsm.Ui;

/// <summary>"New download" dialog: checks the link, then lets the user pick name, folder and connections.</summary>
internal sealed class AddDownloadForm : Form
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly TextBox _url = new();
    private readonly TextBox _name = new();
    private readonly TextBox _folder = new();
    private readonly ComboBox _category = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _connections = new() { Minimum = 1, Maximum = 32, Width = 60 };
    private readonly Label _info = UiKit.Caption("", muted: true);
    private readonly PictureBox _icon = new() { SizeMode = PictureBoxSizeMode.CenterImage };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 600 };
    private CancellationTokenSource? _probe;
    private bool _nameEdited;
    private bool _folderEdited;
    private bool _updating;

    public AddDownloadForm(AppSettings settings, AddRequest prefill, Icon icon)
    {
        _settings = settings;
        _headers = prefill.Headers ?? new Dictionary<string, string>();
        UiKit.Dialog(this, "New download");
        Icon = icon;

        var width = LogicalToDeviceUnits(460);
        _url.Width = width;
        _name.Width = width;
        _folder.Width = width - LogicalToDeviceUnits(100);
        _category.Items.AddRange(Categories.All);
        _connections.Value = Math.Clamp(prefill.Connections ?? settings.DefaultConnections, 1, 32);
        _icon.Size = new Size(LogicalToDeviceUnits(40), LogicalToDeviceUnits(40));
        _icon.Image = Glyphs.Render(Glyphs.Download, LogicalToDeviceUnits(32), Glyphs.Accent);

        var grid = UiKit.Grid(0, 100, 0);
        grid.Controls.Add(_icon, 0, 0);
        grid.SetRowSpan(_icon, 2);
        grid.Controls.Add(_url, 1, 0);
        grid.SetColumnSpan(_url, 2);
        grid.Controls.Add(_info, 1, 1);
        grid.SetColumnSpan(_info, 2);
        grid.Controls.Add(UiKit.Caption("File name"), 0, 2);
        grid.Controls.Add(_name, 1, 2);
        grid.SetColumnSpan(_name, 2);
        grid.Controls.Add(UiKit.Caption("Save to"), 0, 3);
        grid.Controls.Add(_folder, 1, 3);
        grid.Controls.Add(UiKit.Button("Browse…", (_, _) => BrowseFolder()), 2, 3);
        grid.Controls.Add(UiKit.Caption("Category"), 0, 4);
        var options = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
        options.Controls.AddRange([_category, UiKit.Caption("   Connections"), _connections]);
        grid.Controls.Add(options, 1, 4);
        grid.SetColumnSpan(options, 2);

        var start = UiKit.Button("Start download", (_, _) => Finish(startNow: true));
        Theme.Tint(start, ButtonRole.Go);
        var later = UiKit.Button("Download later", (_, _) => Finish(startNow: false));
        var cancel = UiKit.Button("Cancel", (_, _) => Close());
        Controls.Add(grid);
        Controls.Add(UiKit.ButtonRow(cancel, start, later));
        AcceptButton = start;
        CancelButton = cancel;

        _url.Text = prefill.Url;
        _name.Text = prefill.FileName ?? "";
        _nameEdited = !string.IsNullOrEmpty(prefill.FileName);
        _folderEdited = !string.IsNullOrEmpty(prefill.Folder);
        SelectCategory(Categories.For(prefill.FileName ?? Shell.FileNameFromUrl(prefill.Url)));
        if (_folderEdited) _folder.Text = prefill.Folder!;

        _url.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _name.TextChanged += (_, _) => { if (!_updating) _nameEdited = true; };
        _folder.TextChanged += (_, _) => { if (!_updating) _folderEdited = true; };
        _category.SelectedIndexChanged += (_, _) => { if (!_folderEdited) SetFolder(_settings.FolderFor((string)_category.SelectedItem!)); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = CheckLinkAsync(); };
        Shown += (_, _) =>
        {
            TopMost = true; // links from the browser or clipboard should not open behind other windows
            Activate();
            TopMost = false;
            (_url.Text.Length == 0 ? _url : (Control)_name).Focus();
            _ = CheckLinkAsync();
        };
        FormClosed += (_, _) => _probe?.Cancel();
    }

    /// <summary>The request to add, set when the user presses Start download or Download later.</summary>
    public AddRequest? Result { get; private set; }

    private async Task CheckLinkAsync()
    {
        _probe?.Cancel();
        if (Shell.AsHttpUrl(_url.Text) is not { } url)
        {
            if (_url.Text.Trim().Length == 0) ShowInfo("Paste a download link (http or https).", Theme.Muted);
            else ShowInfo("That doesn't look like an http or https link.", Theme.DangerText);
            return;
        }
        var probe = _probe = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        ShowInfo("Checking the link…", Theme.Muted);
        try
        {
            var result = await Http.ProbeAsync(new Uri(url), _headers, probe.Token);
            if (probe != _probe || IsDisposed) return;
            if (!_nameEdited) SetName(result.FileName);
            SelectCategory(Categories.For(_name.Text));
            var size = result.Size is long s ? Format.Bytes(s) : "Unknown size";
            if (result.Resumable) ShowInfo($"{size}  ·  resume supported", Theme.SuccessText);
            else ShowInfo($"{size}  ·  this server can't resume (one connection)", Theme.WarningText);
        }
        catch (Exception ex) when (!IsDisposed && probe == _probe)
        {
            ShowInfo(ex is OperationCanceledException ? "The server didn't answer in time." : $"Couldn't check the link: {Http.Describe(ex)}",
                Theme.DangerText);
        }
    }

    /// <summary>Status line under the address: green fine, yellow limitation, red problem, grey neutral.</summary>
    private void ShowInfo(string text, Color color)
    {
        _info.Text = text;
        _info.ForeColor = color;
    }

    private void SetName(string name)
    {
        _updating = true;
        _name.Text = name;
        _updating = false;
    }

    private void SetFolder(string folder)
    {
        _updating = true;
        _folder.Text = folder;
        _updating = false;
    }

    private void SelectCategory(string category)
    {
        if ((string?)_category.SelectedItem == category) return;
        _category.SelectedItem = category;
        if (!_folderEdited) SetFolder(_settings.FolderFor(category));
    }

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text, UseDescriptionForTitle = true, Description = "Save downloads to" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
    }

    private void Finish(bool startNow)
    {
        if (Shell.AsHttpUrl(_url.Text) is not { } url)
        {
            ShowInfo("Enter an http or https link first.", Theme.DangerText);
            _url.Focus();
            return;
        }
        var name = _name.Text.Trim();
        Result = new AddRequest(url, name.Length > 0 ? Http.Sanitize(name) : null, _folder.Text.Trim(),
            (int)_connections.Value, _headers, startNow);
        DialogResult = DialogResult.OK;
        Close();
    }
}
