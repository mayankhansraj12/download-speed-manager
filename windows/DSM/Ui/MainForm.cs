using Dsm.Engine;
using Dsm.Platform;

namespace Dsm.Ui;

/// <summary>Main window: menu, toolbar, category tree, download list and status bar.</summary>
internal sealed partial class MainForm : Form
{
    private const int ProgressColumn = 2;
    private const int StatusColumn = 5;
    private readonly DateTime _sessionStart = DateTime.Now;
    private (TaskbarState State, int Permille) _taskbar = (TaskbarState.None, -1);
    private readonly Shell _shell;
    private readonly MenuStrip _menu = new();
    private readonly ToolStrip _toolbar = new();
    private readonly ToolStripTextBox _search = new();
    private readonly TreeView _tree = new();
    private readonly SmoothListView _list = new();
    private readonly SplitContainer _split = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _countLabel = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _speedLabel = new();
    private readonly ToolStripStatusLabel _limitLabel = new();
    private readonly ContextMenuStrip _rowMenu = new();
    private readonly UpdateBar _updateBar = new();
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private readonly Dictionary<string, ListViewItem> _rows = [];
    private readonly List<ToolStripItem> _needsSelection = [];
    private string _filter = "All";
    private bool _listDirty = true;

    public MainForm(Shell shell)
    {
        _shell = shell;
        Text = "Download Speed Manager";
        Icon = shell.AppIcon;
        MinimumSize = new Size(LogicalToDeviceUnits(780), LogicalToDeviceUnits(420));
        var area = Screen.PrimaryScreen?.WorkingArea.Size ?? new Size(1280, 800);
        Size = new Size(Math.Min(LogicalToDeviceUnits(1120), area.Width - 40), Math.Min(LogicalToDeviceUnits(660), area.Height - 40));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AllowDrop = true;
        Theme.Apply(this);

        BuildMenu();
        BuildToolbar();
        BuildTree();
        BuildList();
        BuildStatus();

        _split.Dock = DockStyle.Fill;
        _split.FixedPanel = FixedPanel.Panel1;
        _split.SplitterWidth = 1;
        _split.BackColor = Theme.Border;
        _split.Panel1.BackColor = Theme.Sidebar;
        _split.Panel1.Controls.Add(_tree);
        _split.Panel2.Controls.Add(_list);

        // Docking runs from the last control added, so add the fill area first and the menu last.
        Controls.Add(_split);
        Controls.Add(_status);
        Controls.Add(_updateBar);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        _updateBar.InstallClicked += async (_, _) => await _shell.InstallUpdateAsync();
        _updateBar.NotesClicked += (_, _) => _shell.OpenReleaseNotes();
        MainMenuStrip = _menu;

        var queue = shell.Queue;
        queue.ItemAdded += _ => MarkDirty();
        queue.ItemRemoved += _ => MarkDirty();
        queue.ItemChanged += _ => MarkDirty();
        _refresh.Tick += (_, _) => RefreshView();
        _refresh.Start();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _split.Panel1MinSize = 150;
        _split.SplitterDistance = LogicalToDeviceUnits(200);
        RefreshView();
    }

    public void ShowUpdate(Updates.UpdateCheck check, bool portable) =>
        _updateBar.ShowAvailable(check.Manifest!.Version, Updates.UpdateService.CurrentVersion.ToString(3), check.Required, portable);

    public void ShowUpdateProgress(string text) => _updateBar.ShowProgress(text);

    public void ShowUpdateError(string text) => _updateBar.ShowError(text);

    private void MarkDirty()
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            _listDirty = true;
            RefreshView();
        });
    }

    private void BuildTree()
    {
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
        images.Images.Add("All", Glyphs.Render(Glyphs.All, 16));
        images.Images.Add("Unfinished", Glyphs.Render(Glyphs.Clock, 16));
        images.Images.Add("Finished", Glyphs.Render(Glyphs.Check, 16));
        foreach (var category in Categories.All) images.Images.Add(category, Glyphs.Render(Glyphs.ForCategory(category), 16));

        _tree.Dock = DockStyle.Fill;
        _tree.BorderStyle = BorderStyle.None;
        _tree.BackColor = Theme.Sidebar;
        _tree.ForeColor = Theme.Text;
        _tree.ShowLines = false;
        _tree.ShowRootLines = false;
        _tree.FullRowSelect = true;
        _tree.HideSelection = false;
        _tree.ItemHeight = LogicalToDeviceUnits(28);
        _tree.Indent = LogicalToDeviceUnits(14);
        _tree.ImageList = images;
        _tree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        _tree.DrawNode += DrawTreeNode;

        var all = Node("All", "All downloads");
        foreach (var category in Categories.All) all.Nodes.Add(Node(category, category));
        _tree.Nodes.Add(all);
        _tree.Nodes.Add(Node("Unfinished", "Unfinished"));
        _tree.Nodes.Add(Node("Finished", "Finished"));
        all.Expand();
        _tree.SelectedNode = all;
        _tree.AfterSelect += (_, e) =>
        {
            _filter = (string)e.Node!.Tag!;
            MarkDirty();
        };
    }

    /// <summary>Navigation-pane style rows: full-width selection with an accent marker, in theme colours.</summary>
    private void DrawTreeNode(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node is not { } node || e.Bounds.Height == 0) return;
        var g = e.Graphics;
        var row = new Rectangle(0, e.Bounds.Y, _tree.ClientSize.Width, e.Bounds.Height);
        var selected = node.IsSelected;
        using (var back = new SolidBrush(selected ? Theme.Selected : Theme.Sidebar)) g.FillRectangle(back, row);
        if (selected)
        {
            using var marker = new SolidBrush(Theme.Accent);
            g.FillRectangle(marker, 0, row.Y + row.Height / 4, LogicalToDeviceUnits(3), row.Height / 2);
        }
        var x = LogicalToDeviceUnits(12) + node.Level * _tree.Indent;
        var tint = (string)node.Tag! switch
        {
            "Unfinished" => Theme.Warning, // waiting = yellow
            "Finished" => Theme.Success,   // done = green
            _ => selected ? Theme.Accent : (Color?)null,
        };
        var icon = Glyphs.Render(TreeGlyph((string)node.Tag!), LogicalToDeviceUnits(16), tint);
        g.DrawImage(icon, x, row.Y + (row.Height - icon.Height) / 2);
        x += icon.Width + LogicalToDeviceUnits(10);
        TextRenderer.DrawText(g, node.Text, _tree.Font, new Rectangle(x, row.Y, Math.Max(0, row.Width - x), row.Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static char TreeGlyph(string key) => key switch
    {
        "All" => Glyphs.All,
        "Unfinished" => Glyphs.Clock,
        "Finished" => Glyphs.Check,
        _ => Glyphs.ForCategory(key),
    };

    private static TreeNode Node(string key, string text) =>
        new(text) { Tag = key, Name = key, ImageKey = key, SelectedImageKey = key };

    private void BuildList()
    {
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
        foreach (var category in Categories.All) images.Images.Add(category, Glyphs.Render(Glyphs.ForCategory(category), 16, Glyphs.Accent));

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.OwnerDraw = true;
        _list.SmallImageList = images;
        _list.ContextMenuStrip = _rowMenu;
        _list.Columns.Add("File name", LogicalToDeviceUnits(280)); // widened to fill by FitNameColumn
        _list.Columns.Add("Size", LogicalToDeviceUnits(80), HorizontalAlignment.Right);
        _list.Columns.Add("Progress", LogicalToDeviceUnits(140));
        _list.Columns.Add("Speed", LogicalToDeviceUnits(88), HorizontalAlignment.Right);
        _list.Columns.Add("Time left", LogicalToDeviceUnits(78), HorizontalAlignment.Right);
        _list.Columns.Add("Status", LogicalToDeviceUnits(150));
        _list.ShowItemToolTips = true; // full error text (or the link) on hover
        _list.Columns.Add("Added", LogicalToDeviceUnits(118));
        _list.BackColor = Theme.Window;
        _list.ForeColor = Theme.Text;
        _list.DrawColumnHeader += (_, e) => Theme.DrawHeader(e, _list.Font);
        _list.DrawItem += (_, _) => { };
        _list.DrawSubItem += DrawCell;
        _list.DoubleClick += (_, _) => OpenOrShowSelected();
        _list.Resize += (_, _) => FitNameColumn();
        _list.SelectedIndexChanged += (_, _) => UpdateCommands();
        BuildRowMenu();
    }

    /// <summary>The file-name column takes whatever width the other columns leave (no horizontal scrollbar).</summary>
    private void FitNameColumn()
    {
        if (_list.Columns.Count == 0) return;
        var others = _list.Columns.Cast<ColumnHeader>().Skip(1).Sum(c => c.Width);
        var width = Math.Max(LogicalToDeviceUnits(180), _list.ClientSize.Width - others - 2);
        if (_list.Columns[0].Width != width) _list.Columns[0].Width = width;
    }

    private void BuildStatus()
    {
        _status.Renderer = Theme.Renderer;
        _status.SizingGrip = true;
        _speedLabel.Image = Glyphs.Render(Glyphs.Download, 16, Glyphs.Accent);
        _limitLabel.Image = Glyphs.Render(Glyphs.Speed, 16);
        _status.Items.AddRange([_countLabel, _speedLabel, new ToolStripSeparator(), _limitLabel]);
    }

    /// <summary>
    /// Progress and Status cells are coloured by state (green running or done, yellow paused or
    /// waiting, red failed); other cells use default drawing.
    /// </summary>
    private void DrawCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex is not (ProgressColumn or StatusColumn) || e.Item?.Tag is not DownloadItem d)
        {
            e.DrawDefault = true;
            return;
        }
        var g = e.Graphics;
        var highlighted = e.Item.Selected && _list.Focused;
        var back = e.Item.Selected ? (_list.Focused ? SystemColors.Highlight : Theme.Selected) : _list.BackColor;
        using (var brush = new SolidBrush(back)) g.FillRectangle(brush, e.Bounds);

        if (e.ColumnIndex == StatusColumn)
        {
            TextRenderer.DrawText(g, e.SubItem?.Text, _list.Font, Rectangle.Inflate(e.Bounds, -6, 0),
                highlighted ? SystemColors.HighlightText : Theme.TextForStatus(d.Status),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            return;
        }

        var textWidth = LogicalToDeviceUnits(46);
        var bar = new Rectangle(e.Bounds.X + 6, e.Bounds.Y + e.Bounds.Height / 2 - 4, Math.Max(10, e.Bounds.Width - textWidth - 12), 8);
        using (var track = new SolidBrush(Theme.Track)) g.FillRectangle(track, bar);
        if (d.Fraction is double fraction)
        {
            using var fill = new SolidBrush(Theme.ForStatus(d.Status));
            g.FillRectangle(fill, bar.X, bar.Y, (int)(bar.Width * fraction), bar.Height);
        }
        var textRect = new Rectangle(bar.Right + 4, e.Bounds.Y, textWidth, e.Bounds.Height);
        TextRenderer.DrawText(g, Format.Percent(d), _list.Font, textRect, highlighted ? SystemColors.HighlightText : Theme.Text,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private void RefreshView()
    {
        var items = _shell.Queue.Items;
        if (_listDirty) RebuildList(items);
        foreach (var d in items)
        {
            if (_rows.TryGetValue(d.Id, out var row)) UpdateRow(row, d);
        }
        UpdateTree(items);
        UpdateStatusBar(items);
        UpdateCommands();
    }

    private bool Matches(DownloadItem d)
    {
        var inFilter = _filter switch
        {
            "All" => true,
            "Unfinished" => d.Status != DownloadStatus.Completed,
            "Finished" => d.Status == DownloadStatus.Completed,
            _ => d.Category == _filter,
        };
        var query = _search.Text.Trim();
        return inFilter && (query.Length == 0 || d.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void RebuildList(IReadOnlyList<DownloadItem> items)
    {
        _listDirty = false;
        var selected = Selected().Select(d => d.Id).ToHashSet();
        _list.BeginUpdate();
        _list.Items.Clear();
        _rows.Clear();
        foreach (var d in items.Where(Matches).OrderByDescending(d => d.CreatedAt))
        {
            var row = new ListViewItem(Enumerable.Repeat("", _list.Columns.Count).ToArray()) { Tag = d };
            UpdateRow(row, d);
            _rows[d.Id] = row;
            _list.Items.Add(row);
            row.Selected = selected.Contains(d.Id);
        }
        _list.EndUpdate();
    }

    private static void UpdateRow(ListViewItem row, DownloadItem d)
    {
        var downloading = d.Status == DownloadStatus.Downloading;
        Set(row, 0, d.DisplayName);
        Set(row, 1, d.Size is null && d.Status != DownloadStatus.Completed ? "" : Format.Bytes(d.Size ?? d.Received));
        Set(row, 2, Format.Percent(d));
        Set(row, 3, downloading ? Format.Speed(d.Speed) : "");
        Set(row, 4, downloading ? Format.Eta(d.EtaSeconds) : "");
        Set(row, 5, d.Status == DownloadStatus.Error ? $"Error: {d.Error}" : Format.Status(d));
        Set(row, 6, Format.Date(d.CreatedAt));
        if (row.ImageKey != d.Category) row.ImageKey = d.Category;
        var tip = d.Status == DownloadStatus.Error ? $"{d.Error}\n{d.Url}" : d.Url;
        if (row.ToolTipText != tip) row.ToolTipText = tip;
    }

    private static void Set(ListViewItem row, int column, string text)
    {
        if (row.SubItems[column].Text != text) row.SubItems[column].Text = text;
    }

    private void UpdateTree(IReadOnlyList<DownloadItem> items)
    {
        void Label(TreeNode node, string text, int count)
        {
            var label = count > 0 ? $"{text}  ({count})" : text;
            if (node.Text != label) node.Text = label;
        }
        var all = _tree.Nodes["All"]!;
        Label(all, "All downloads", items.Count);
        foreach (TreeNode node in all.Nodes) Label(node, (string)node.Tag!, items.Count(d => d.Category == (string)node.Tag!));
        Label(_tree.Nodes["Unfinished"]!, "Unfinished", items.Count(d => d.Status != DownloadStatus.Completed));
        Label(_tree.Nodes["Finished"]!, "Finished", items.Count(d => d.Status == DownloadStatus.Completed));
    }

    private void UpdateStatusBar(IReadOnlyList<DownloadItem> items)
    {
        var active = items.Count(d => d.IsActive);
        var speed = items.Where(d => d.Status == DownloadStatus.Downloading).Sum(d => d.Speed);
        _countLabel.Text = $"{items.Count} downloads, {active} active";
        _speedLabel.Text = speed > 0 ? Format.Speed(speed) : "0 B/s";
        Tint(_speedLabel, Glyphs.Download, speed > 0 ? Theme.Success : null, speed > 0 ? Theme.SuccessText : null);
        var limit = _shell.Settings.GlobalSpeedLimit;
        _limitLabel.Text = limit > 0 ? $"Limit {Format.Speed(limit)}" : "No speed limit";
        Tint(_limitLabel, Glyphs.Speed, limit > 0 ? Theme.Warning : null, limit > 0 ? Theme.WarningText : null);
        var title = active > 0 ? $"{Format.Speed(speed)} – Download Speed Manager" : "Download Speed Manager";
        if (Text != title) Text = title;
        UpdateTaskbar(items);
    }

    /// <summary>Status-bar label with its icon and text in a status colour (or neutral).</summary>
    private void Tint(ToolStripStatusLabel label, char glyph, Color? icon, Color? text)
    {
        var image = Glyphs.Render(glyph, LogicalToDeviceUnits(16), icon);
        if (label.Image != image) label.Image = image;
        if (Equals(label.Tag, text)) return;
        label.Tag = text;
        label.Invalidate();
    }

    /// <summary>
    /// Overall progress on the taskbar button, in Windows' colours: green while downloading,
    /// yellow when this session's downloads are paused, red after an error.
    /// </summary>
    private void UpdateTaskbar(IReadOnlyList<DownloadItem> items)
    {
        var session = items.Where(d => d.LastTryAt >= _sessionStart).ToList();
        var running = session.Where(d => d.IsActive).ToList();
        var waiting = session.Where(d => d.Status is DownloadStatus.Paused or DownloadStatus.Queued).ToList();
        (TaskbarState State, double Fraction) target =
            running.Count > 0 ? (running.All(d => d.Size is null) ? TaskbarState.Indeterminate : TaskbarState.Normal, Progress(running))
            : session.Any(d => d.Status == DownloadStatus.Error) ? (TaskbarState.Error, 1)
            : waiting.Count > 0 ? (TaskbarState.Paused, Progress(waiting))
            : (TaskbarState.None, 0);
        var permille = (int)Math.Round(target.Fraction * 1000);
        if (_taskbar == (target.State, permille)) return;
        _taskbar = (target.State, permille);
        Taskbar.Set(this, target.State, target.Fraction);
    }

    private static double Progress(IEnumerable<DownloadItem> items)
    {
        var known = items.Where(d => d.Size is > 0).ToList();
        return known.Count == 0 ? 0 : known.Sum(d => (double)d.Received) / known.Sum(d => (double)d.Size!.Value);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _taskbar = (TaskbarState.None, -1); // the taskbar button is recreated when the window is shown again
    }
}
