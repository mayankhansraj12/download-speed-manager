using Dsm.Engine;
using Dsm.Platform;

namespace Dsm.Ui;

/// <summary>Main window: menu, toolbar, category tree, download list and status bar.</summary>
internal sealed partial class MainForm : Form
{
    private const int SizeColumn = 1;
    private const int ProgressColumn = 2;
    private const int StatusColumn = 5;
    private const int AddedColumn = 6;
    private readonly DateTime _sessionStart = DateTime.Now;
    private (TaskbarState State, int Permille) _taskbar = (TaskbarState.None, -1);
    private readonly Shell _shell;
    private readonly MenuStrip _menu = new();
    private readonly ToolStrip _toolbar = new();
    private readonly SearchBox _search = new("Search downloads", "Ctrl+F");
    private readonly NavTree _tree = new();
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
    private int _hoverRow = -1;
    private TreeNode? _hoverNode;
    private bool _fittingColumns;
    private bool _statusResized; // the user dragged the Status column: stop sizing it automatically

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
        _tree.MouseMove += (_, e) => SetHoverNode(_tree.GetNodeAt(e.Location));
        _tree.MouseLeave += (_, _) => SetHoverNode(null);
        _tree.GotFocus += (_, _) => InvalidateNode(_tree.SelectedNode);
        _tree.LostFocus += (_, _) => InvalidateNode(_tree.SelectedNode);
    }

    private void SetHoverNode(TreeNode? node)
    {
        if (node == _hoverNode) return;
        InvalidateNode(_hoverNode);
        _hoverNode = node;
        InvalidateNode(node);
    }

    private void InvalidateNode(TreeNode? node)
    {
        if (node is { IsVisible: true }) _tree.Invalidate(new Rectangle(0, node.Bounds.Y, _tree.ClientSize.Width, node.Bounds.Height));
    }

    /// <summary>Navigation-pane style rows: full-width selection with an accent marker, in theme colours.</summary>
    private void DrawTreeNode(object? sender, DrawTreeNodeEventArgs e)
    {
        if (e.Node is not { } node || e.Bounds.Height == 0) return;
        var g = e.Graphics;
        var row = new Rectangle(0, e.Bounds.Y, _tree.ClientSize.Width, e.Bounds.Height);
        var selected = node.IsSelected;
        var fill = selected ? Theme.Selected : node == _hoverNode ? Theme.NavHover : Theme.Sidebar;
        using (var back = new SolidBrush(fill)) g.FillRectangle(back, row);
        if (selected)
        {
            using var marker = new SolidBrush(Theme.Accent);
            g.FillRectangle(marker, 0, row.Y + row.Height / 4, LogicalToDeviceUnits(3), row.Height / 2);
            if (_tree.Focused && _tree.FocusCuesVisible)
            {
                using var outline = new Pen(Theme.Accent); // keyboard focus, as in the download list
                g.DrawRectangle(outline, row.X, row.Y, row.Width - 1, row.Height - 1);
            }
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
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.OwnerDraw = true;
        // Rows are drawn in DrawCell (icons included); this image list only sets a roomier row height.
        _list.SmallImageList = new ImageList { ImageSize = new Size(1, LogicalToDeviceUnits(26)) };
        _list.ContextMenuStrip = _rowMenu;
        _list.Columns.Add("File name", LogicalToDeviceUnits(280)); // File name and Status are sized by FitColumns
        _list.Columns.Add("Size", LogicalToDeviceUnits(80), HorizontalAlignment.Right);
        _list.Columns.Add("Progress", LogicalToDeviceUnits(140));
        _list.Columns.Add("Speed", LogicalToDeviceUnits(88), HorizontalAlignment.Right);
        _list.Columns.Add("Time left", LogicalToDeviceUnits(78), HorizontalAlignment.Right);
        _list.Columns.Add("Status", LogicalToDeviceUnits(150));
        _list.ShowItemToolTips = true; // full error text (or the link) on hover
        _list.Columns.Add("Added", DateColumnWidth());
        _list.BackColor = Theme.Window;
        _list.ForeColor = Theme.Text;
        _list.DrawColumnHeader += (_, e) => Theme.DrawHeader(e, _list.Font);
        _list.DrawItem += (_, _) => { };
        _list.DrawSubItem += DrawCell;
        _list.DoubleClick += (_, _) => OpenOrShowSelected();
        _list.Resize += (_, _) => FitColumns();
        _list.ColumnWidthChanged += (_, e) =>
        {
            if (!_fittingColumns && e.ColumnIndex == StatusColumn && Control.MouseButtons.HasFlag(System.Windows.Forms.MouseButtons.Left))
                _statusResized = true;
            _list.Invalidate(); // owner-drawn cells depend on the column width
        };
        _list.SelectedIndexChanged += (_, _) => UpdateCommands();
        // Selection is tinted with the accent while the list has focus and grey otherwise.
        _list.GotFocus += (_, _) => _list.Invalidate();
        _list.LostFocus += (_, _) => _list.Invalidate();
        _list.MouseMove += (_, e) => SetHoverRow(_list.GetItemAt(e.X, e.Y)?.Index ?? -1);
        _list.MouseLeave += (_, _) => SetHoverRow(-1);
        BuildRowMenu();
    }

    /// <summary>Wide enough for any date in the user's short date and time format.</summary>
    private int DateColumnWidth()
    {
        var widest = Enumerable.Range(1, 12)
            .SelectMany(month => new[] { new DateTime(2026, month, 28, 10, 58, 0), new DateTime(2026, month, 28, 22, 58, 0) })
            .Max(date => TextRenderer.MeasureText(Format.Date(date), _list.Font).Width);
        return widest + LogicalToDeviceUnits(14);
    }

    /// <summary>
    /// Columns fill the list without a horizontal scrollbar: Status gets part of any spare room
    /// (unless the user sized it) so error messages show more, and File name takes the rest.
    /// </summary>
    private void FitColumns()
    {
        if (_list.Columns.Count == 0) return;
        _fittingColumns = true;
        try
        {
            var name = _list.Columns[0];
            var status = _list.Columns[StatusColumn];
            var others = _list.Columns.Cast<ColumnHeader>().Where(c => c != name && c != status).Sum(c => c.Width);
            var free = _list.ClientSize.Width - others - 2;
            if (!_statusResized)
            {
                var spare = free - LogicalToDeviceUnits(240) - LogicalToDeviceUnits(150); // File name gets 240 first
                var width = LogicalToDeviceUnits(150) + Math.Clamp(spare * 2 / 5, 0, LogicalToDeviceUnits(200));
                if (status.Width != width) status.Width = width;
            }
            var nameWidth = Math.Max(LogicalToDeviceUnits(180), free - status.Width);
            if (name.Width != nameWidth) name.Width = nameWidth;
        }
        finally
        {
            _fittingColumns = false;
        }
    }

    private void SetHoverRow(int index)
    {
        if (index == _hoverRow) return;
        InvalidateRow(_hoverRow);
        _hoverRow = index;
        InvalidateRow(_hoverRow);
    }

    private void InvalidateRow(int index)
    {
        if (index >= 0 && index < _list.Items.Count) _list.Invalidate(_list.Items[index].Bounds);
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
    /// Every cell is drawn here so rows look the same in both themes: hover and selection tints
    /// (with an accent marker while the list has focus), the category icon, a progress bar and a
    /// Status coloured by state (green running or done, yellow paused or waiting, red failed).
    /// </summary>
    private void DrawCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is not { Tag: DownloadItem d } row)
        {
            e.DrawDefault = true;
            return;
        }
        var g = e.Graphics;
        var cell = e.Bounds;
        var focused = _list.Focused;
        var back = Theme.RowBack(row.Selected, focused, row.Index == _hoverRow, _list.BackColor);
        using (var brush = new SolidBrush(back)) g.FillRectangle(brush, cell);
        var pad = LogicalToDeviceUnits(6);
        const TextFormatFlags Line = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        switch (e.ColumnIndex)
        {
            case 0:
                if (row.Selected && focused)
                {
                    using var marker = new SolidBrush(Theme.Accent);
                    g.FillRectangle(marker, cell.X, cell.Y + cell.Height / 4, LogicalToDeviceUnits(3), cell.Height / 2);
                }
                var icon = Glyphs.Render(Glyphs.ForCategory(d.Category), LogicalToDeviceUnits(16), Glyphs.Accent);
                var x = cell.X + LogicalToDeviceUnits(10);
                g.DrawImage(icon, x, cell.Y + (cell.Height - icon.Height) / 2);
                x += icon.Width + LogicalToDeviceUnits(6);
                var nameArea = new Rectangle(x, cell.Y, Math.Max(0, cell.Right - x - pad), cell.Height);
                TextRenderer.DrawText(g, FitMiddle(g, e.SubItem?.Text ?? "", nameArea.Width), _list.Font, nameArea, Theme.Text, Line);
                break;
            case ProgressColumn:
                var textWidth = LogicalToDeviceUnits(44);
                var height = LogicalToDeviceUnits(6);
                var bar = new Rectangle(cell.X + pad, cell.Y + (cell.Height - height) / 2,
                    Math.Max(LogicalToDeviceUnits(10), cell.Width - 2 * pad - textWidth - LogicalToDeviceUnits(6)), height);
                Theme.DrawBar(g, bar, d.Fraction ?? 0, Theme.ForStatus(d.Status), Theme.TrackOn(back));
                TextRenderer.DrawText(g, e.SubItem?.Text, _list.Font, new Rectangle(cell.Right - pad - textWidth, cell.Y, textWidth, cell.Height),
                    Theme.Text, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                break;
            default:
                var color = e.ColumnIndex switch
                {
                    StatusColumn => Theme.TextForStatus(d.Status),
                    SizeColumn or AddedColumn when !row.Selected => Theme.Muted, // details, quieter than the name
                    _ => Theme.Text,
                };
                var align = _list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
                TextRenderer.DrawText(g, e.SubItem?.Text, _list.Font, Rectangle.Inflate(cell, -pad, 0), color, Line | align);
                break;
        }

        // Keyboard focus: an accent outline around the focused row, each cell drawing its part.
        if (focused && row.Focused && _list.FocusCuesVisible)
        {
            using var pen = new Pen(Theme.Accent);
            var bottom = cell.Bottom - 1;
            g.DrawLine(pen, cell.Left, cell.Top, cell.Right - 1, cell.Top);
            g.DrawLine(pen, cell.Left, bottom, cell.Right - 1, bottom);
            if (e.ColumnIndex == 0) g.DrawLine(pen, cell.Left, cell.Top, cell.Left, bottom);
            if (e.ColumnIndex == _list.Columns.Count - 1) g.DrawLine(pen, cell.Right - 1, cell.Top, cell.Right - 1, bottom);
        }
    }

    /// <summary>
    /// A file name that is too wide loses its middle ("long-file-na…remaster.flac"), so the
    /// extension and version at the end stay readable.
    /// </summary>
    private string FitMiddle(Graphics g, string name, int width)
    {
        const TextFormatFlags Flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        int Width(string text) => TextRenderer.MeasureText(g, text, _list.Font, new Size(int.MaxValue, int.MaxValue), Flags).Width;
        if (width <= 0 || Width(name) <= width) return name;
        var tail = Math.Min(name.Length / 2, 14);
        int lo = 0, hi = name.Length - tail; // longest start that still fits
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Width(string.Concat(name.AsSpan(0, mid), "…", name.AsSpan(name.Length - tail))) <= width) lo = mid;
            else hi = mid - 1;
        }
        return lo < 4 ? name : string.Concat(name.AsSpan(0, lo), "…", name.AsSpan(name.Length - tail)); // too narrow: plain end ellipsis
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
        _hoverRow = -1;
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
        UpdateEmptyMessage(items.Count);
    }

    /// <summary>What the list says when nothing is shown: why, and what to do next.</summary>
    private void UpdateEmptyMessage(int total)
    {
        var query = _search.Text.Trim();
        var (glyph, title, hint) =
            query.Length > 0 ? (Glyphs.Search, $"No downloads match \"{query}\"",
                _filter == "All" ? "Try another part of the file name." : "Try another part of the file name, or choose All downloads.")
            : total == 0 ? (Glyphs.Download, "No downloads yet", "Click Add URL or press Ctrl+N to download a file.\nYou can also drop a link here.")
            : _filter switch
            {
                "Unfinished" => (TreeGlyph(_filter), "No unfinished downloads", "Everything you added has finished."),
                "Finished" => (TreeGlyph(_filter), "No finished downloads yet", "Downloads show up here once they complete."),
                _ => (TreeGlyph(_filter), $"No downloads in {_filter}", "Choose All downloads to see everything."),
            };
        _list.SetEmptyMessage(glyph, title, hint);
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
