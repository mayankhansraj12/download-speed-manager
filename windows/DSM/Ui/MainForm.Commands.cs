using Dsm.Engine;

namespace Dsm.Ui;

internal sealed partial class MainForm
{
    private static readonly (string Label, long Bytes)[] SpeedPresets =
    [
        ("Unlimited", 0), ("256 KB/s", 256 * 1024), ("512 KB/s", 512 * 1024),
        ("1 MB/s", 1024 * 1024), ("2 MB/s", 2 * 1024 * 1024), ("5 MB/s", 5 * 1024 * 1024),
    ];

    private void BuildMenu()
    {
        _menu.Renderer = Theme.Renderer;
        _menu.Padding = new Padding(6, 2, 0, 2);

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.AddRange(
        [
            Item("&Add URL…", Glyphs.Add, (_, _) => _shell.ShowAddDialog(), Keys.Control | Keys.N),
            Item("Add from &clipboard", Glyphs.Link, (_, _) => AddFromClipboard(), Keys.Control | Keys.Shift | Keys.V),
            new ToolStripSeparator(),
            Item("E&xit", null, (_, _) => _shell.Exit()),
        ]);

        var downloads = new ToolStripMenuItem("&Downloads");
        downloads.DropDownItems.AddRange(
        [
            NeedsSelection(Item("&Resume", Glyphs.Play, (_, _) => ResumeSelected(), tint: Theme.Success)),
            NeedsSelection(Item("&Pause", Glyphs.Pause, (_, _) => PauseSelected(), tint: Theme.Warning)),
            Item("Resume a&ll", Glyphs.Play, (_, _) => _shell.Queue.ResumeAll(), tint: Theme.Success),
            Item("Pause al&l", Glyphs.Stop, async (_, _) => await _shell.Queue.PauseAllAsync(), tint: Theme.Warning),
            new ToolStripSeparator(),
            NeedsSelection(Item("&Delete…", Glyphs.Delete, (_, _) => DeleteSelected(), keyText: "Del", tint: Theme.Danger)),
            Item("Clear &finished", Glyphs.Clean, (_, _) => RemoveCompleted()),
            new ToolStripSeparator(),
            NeedsSelection(Item("&Open", Glyphs.OpenFile, (_, _) => OpenSelected(), keyText: "Enter")),
            NeedsSelection(Item("Open f&older", Glyphs.Folder, (_, _) => OpenFolder())),
            NeedsSelection(Item("&Copy link", Glyphs.Copy, (_, _) => CopyLink())),
            NeedsSelection(Item("Proper&ties", Glyphs.Info, (_, _) => ShowSelectedProgress())),
        ]);

        var speed = new ToolStripMenuItem("&Speed limit", Glyphs.Render(Glyphs.Speed, 16));
        speed.DropDownOpening += (_, _) => FillSpeedMenu(speed.DropDownItems, _shell);
        FillSpeedMenu(speed.DropDownItems, _shell);
        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.AddRange(
        [
            Item("&Options…", Glyphs.Settings, (_, _) => _shell.ShowOptions()),
            speed,
            Item("&Browser integration…", Glyphs.Link, (_, _) => _shell.ShowOptions(OptionsTab.Browser)),
        ]);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(Item("Check for &updates…", Glyphs.Download, async (_, _) => await _shell.CheckForUpdatesNowAsync()));
        help.DropDownItems.Add(Item("&About Download Speed Manager", Glyphs.Info, (_, _) => ShowAbout()));

        _menu.Items.AddRange([file, downloads, tools, help]);
    }

    private void BuildToolbar()
    {
        var size = LogicalToDeviceUnits(24);
        _toolbar.Renderer = Theme.Renderer;
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.ImageScalingSize = new Size(size, size);
        _toolbar.Padding = new Padding(8, 4, 8, 4);

        ToolStripButton Button(string text, char glyph, EventHandler onClick, Color? tint = null) =>
            new(text, Glyphs.Render(glyph, size, tint), onClick)
            {
                TextImageRelation = TextImageRelation.ImageAboveText,
                Padding = new Padding(6, 2, 6, 2),
            };

        var speed = new ToolStripDropDownButton("Speed limit", Glyphs.Render(Glyphs.Speed, size))
        {
            TextImageRelation = TextImageRelation.ImageAboveText,
            Padding = new Padding(6, 2, 6, 2),
        };
        speed.DropDownOpening += (_, _) => FillSpeedMenu(speed.DropDownItems, _shell);
        FillSpeedMenu(speed.DropDownItems, _shell);

        _toolbar.Items.AddRange(
        [
            Button("Add URL", Glyphs.Add, (_, _) => _shell.ShowAddDialog(), Theme.Accent),
            NeedsSelection(Button("Resume", Glyphs.Play, (_, _) => ResumeSelected(), Theme.Success)),
            NeedsSelection(Button("Pause", Glyphs.Pause, (_, _) => PauseSelected(), Theme.Warning)),
            Button("Pause all", Glyphs.Stop, async (_, _) => await _shell.Queue.PauseAllAsync(), Theme.Warning),
            NeedsSelection(Button("Delete", Glyphs.Delete, (_, _) => DeleteSelected(), Theme.Danger)),
            Button("Clear finished", Glyphs.Clean, (_, _) => RemoveCompleted()),
            new ToolStripSeparator(),
            speed,
            Button("Options", Glyphs.Settings, (_, _) => _shell.ShowOptions()),
        ]);

        _search.Alignment = ToolStripItemAlignment.Right;
        _search.AutoSize = false;
        _search.Width = LogicalToDeviceUnits(220);
        _search.TextBox.PlaceholderText = "Search downloads";
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.TextChanged += (_, _) => MarkDirty();
        _toolbar.Items.Add(_search);
    }

    private void BuildRowMenu()
    {
        _rowMenu.Renderer = Theme.Renderer;
        _rowMenu.Items.AddRange(
        [
            Item("Open", Glyphs.OpenFile, (_, _) => OpenSelected()),
            Item("Open folder", Glyphs.Folder, (_, _) => OpenFolder()),
            new ToolStripSeparator(),
            Item("Resume", Glyphs.Play, (_, _) => ResumeSelected(), tint: Theme.Success),
            Item("Pause", Glyphs.Pause, (_, _) => PauseSelected(), tint: Theme.Warning),
            new ToolStripSeparator(),
            Item("Copy link", Glyphs.Copy, (_, _) => CopyLink()),
            Item("Delete…", Glyphs.Delete, (_, _) => DeleteSelected(), tint: Theme.Danger),
            new ToolStripSeparator(),
            Item("Properties", Glyphs.Info, (_, _) => ShowSelectedProgress()),
        ]);
        _rowMenu.Opening += (_, e) => e.Cancel = _list.SelectedItems.Count == 0;
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.Enter) OpenSelected();
            else if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem row in _list.Items) row.Selected = true;
            }
        };
        _list.AllowDrop = true;
        _list.DragEnter += (_, e) => OnDragEnter(e);
        _list.DragDrop += (_, e) => OnDragDrop(e);
    }

    private static ToolStripMenuItem Item(string text, char? glyph, EventHandler onClick, Keys keys = Keys.None,
        string? keyText = null, Color? tint = null)
    {
        var item = new ToolStripMenuItem(text, glyph is char g ? Glyphs.Render(g, 16, tint) : null, onClick) { ShortcutKeys = keys };
        if (keyText is not null) item.ShortcutKeyDisplayString = keyText;
        return item;
    }

    private ToolStripItem NeedsSelection(ToolStripItem item)
    {
        _needsSelection.Add(item);
        return item;
    }

    internal static void FillSpeedMenu(ToolStripItemCollection items, Shell shell)
    {
        items.Clear();
        foreach (var (label, bytes) in SpeedPresets)
        {
            items.Add(new ToolStripMenuItem(label, null, (_, _) => shell.SetGlobalSpeedLimit(bytes))
            {
                Checked = shell.Settings.GlobalSpeedLimit == bytes,
            });
        }
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Custom…", null, (_, _) => shell.ShowOptions(OptionsTab.Connection)));
    }

    private List<DownloadItem> Selected() =>
        [.. _list.SelectedItems.Cast<ListViewItem>().Select(row => (DownloadItem)row.Tag!)];

    private void UpdateCommands()
    {
        var any = _list.SelectedItems.Count > 0;
        foreach (var item in _needsSelection)
        {
            if (item.Enabled != any) item.Enabled = any;
        }
    }

    private void ResumeSelected()
    {
        foreach (var d in Selected().Where(d => d.Status != DownloadStatus.Completed && !d.IsRunning)) _shell.Queue.Resume(d);
    }

    private async void PauseSelected() => await Task.WhenAll(Selected().Select(d => d.PauseAsync()));

    private async void DeleteSelected()
    {
        var items = Selected();
        if (items.Count == 0) return;
        var delete = new TaskDialogButton("Delete");
        var alsoFiles = new TaskDialogVerificationCheckBox("Also delete downloaded files from disk");
        var page = new TaskDialogPage
        {
            Caption = "Download Speed Manager",
            Heading = items.Count == 1 ? $"Delete \"{items[0].DisplayName}\"?" : $"Delete {items.Count} downloads?",
            Text = "Unfinished downloads are stopped and their partial files removed.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { delete, TaskDialogButton.Cancel },
            Verification = alsoFiles,
        };
        if (TaskDialog.ShowDialog(this, page) != delete) return;
        foreach (var d in items)
        {
            _shell.CloseProgress(d);
            await _shell.Queue.RemoveAsync(d, alsoFiles.Checked);
        }
    }

    private void RemoveCompleted()
    {
        foreach (var d in _shell.Queue.Items.Where(d => d.Status == DownloadStatus.Completed)) _ = _shell.Queue.RemoveAsync(d, deleteFile: false);
    }

    private void OpenSelected()
    {
        var d = Selected().FirstOrDefault();
        if (d is null) return;
        if (d.Status == DownloadStatus.Completed && File.Exists(d.TargetPath)) Shell.Open(d.TargetPath!);
        else _shell.ShowProgress(d);
    }

    private void OpenOrShowSelected() => OpenSelected();

    private void OpenFolder()
    {
        if (Selected().FirstOrDefault() is { } d) Shell.Reveal(d);
    }

    private void CopyLink()
    {
        if (Selected().FirstOrDefault() is { } d) _shell.CopyToClipboard(d.Url);
    }

    private void ShowSelectedProgress()
    {
        foreach (var d in Selected().Take(5)) _shell.ShowProgress(d);
    }

    private void AddFromClipboard()
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        _shell.ShowAddDialog(Shell.AsHttpUrl(text));
    }

    private void ShowAbout()
    {
        TaskDialog.ShowDialog(this, new TaskDialogPage
        {
            Caption = "About",
            Heading = $"Download Speed Manager {Updates.UpdateService.CurrentVersion.ToString(3)}",
            Text = "Multi-connection downloads with speed-aware segmentation, resume and browser capture.\n\n" +
                   (Updates.UpdateService.Repository is { } repo ? $"Updates from github.com/{repo}\n" : "Built locally (no update source)\n") +
                   "MIT License.",
            Icon = TaskDialogIcon.Information,
        });
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        e.Effect = DroppedUrl(e.Data) is null ? DragDropEffects.None : DragDropEffects.Copy;
        base.OnDragEnter(e);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        if (DroppedUrl(e.Data) is { } url) _shell.ShowAddDialog(url);
        base.OnDragDrop(e);
    }

    private static string? DroppedUrl(IDataObject? data) =>
        Shell.AsHttpUrl((data?.GetData(DataFormats.UnicodeText) ?? data?.GetData(DataFormats.Text)) as string);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !_shell.IsExiting)
        {
            e.Cancel = true;
            if (_shell.Settings.CloseToTray)
            {
                Hide();
                _shell.NotifyHidden();
            }
            else
            {
                _shell.Exit();
            }
            return;
        }
        base.OnFormClosing(e);
    }
}
