using Dsm.Engine;
using Dsm.Platform;

namespace Dsm.Ui;

/// <summary>Per-download window: live numbers, segment map and connection details.</summary>
internal sealed class ProgressForm : Form
{
    private readonly Shell _shell;
    private readonly DownloadItem _item;
    private readonly Label _status = Value();
    private readonly Label _size = Value();
    private readonly Label _downloaded = Value();
    private readonly Label _speed = Value();
    private readonly Label _eta = Value();
    private readonly Label _resume = Value();
    private readonly Meter _bar = new() { Dock = DockStyle.Fill };
    private readonly SegmentBar _map = new() { Dock = DockStyle.Fill };
    private readonly SmoothListView _connections = new();
    private readonly CheckBox _limitOn = new() { Text = "Limit this download to", AutoSize = true };
    private readonly NumericUpDown _limit = new() { Minimum = 16, Maximum = 1_000_000, Increment = 64, Width = 90 };
    private readonly CheckBox _closeWhenDone = new() { Text = "Close this window when the download finishes", AutoSize = true, Checked = true };
    private readonly Button _pause;
    private readonly Button _details;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };

    public ProgressForm(Shell shell, DownloadItem item)
    {
        _shell = shell;
        _item = item;
        UiKit.Dialog(this, item.DisplayName);
        Icon = shell.AppIcon;
        MinimizeBox = true;

        var header = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top, Padding = new Padding(16, 14, 16, 4) };
        var icon = new PictureBox
        {
            Image = Glyphs.Render(Glyphs.ForCategory(item.Category), LogicalToDeviceUnits(32), Glyphs.Accent),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Size = new Size(LogicalToDeviceUnits(40), LogicalToDeviceUnits(40)),
            Margin = new Padding(0, 0, 10, 0),
        };
        var title = new Label { Text = item.DisplayName, AutoSize = true, Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), MaximumSize = new Size(LogicalToDeviceUnits(470), 0) };
        var url = new Label { Text = item.Url, AutoSize = false, AutoEllipsis = true, ForeColor = Theme.Muted, Size = new Size(LogicalToDeviceUnits(470), LogicalToDeviceUnits(18)) };
        header.Controls.Add(icon, 0, 0);
        header.SetRowSpan(icon, 2);
        header.Controls.Add(title, 1, 0);
        header.Controls.Add(url, 1, 1);

        var grid = UiKit.Grid(0, 100);
        void Row(string caption, Label value)
        {
            grid.Controls.Add(UiKit.Caption(caption, muted: true));
            grid.Controls.Add(value);
        }
        Row("Status", _status);
        Row("File size", _size);
        Row("Downloaded", _downloaded);
        Row("Transfer rate", _speed);
        Row("Time left", _eta);
        Row("Resume support", _resume);
        grid.Controls.Add(_bar);
        grid.SetColumnSpan(_bar, 2);
        grid.Controls.Add(_map);
        grid.SetColumnSpan(_map, 2);
        _bar.Margin = new Padding(0, 12, 0, 6);
        _bar.Height = LogicalToDeviceUnits(8);
        _map.Margin = new Padding(0, 0, 0, 8);
        _map.Height = LogicalToDeviceUnits(16);

        _connections.View = View.Details;
        _connections.FullRowSelect = true;
        _connections.Height = LogicalToDeviceUnits(150);
        _connections.Dock = DockStyle.Fill;
        _connections.Visible = false;
        _connections.Columns.Add("#", LogicalToDeviceUnits(36));
        _connections.Columns.Add("Downloaded", LogicalToDeviceUnits(130), HorizontalAlignment.Right);
        _connections.Columns.Add("Speed", LogicalToDeviceUnits(110), HorizontalAlignment.Right);
        _connections.Columns.Add("State", LogicalToDeviceUnits(170));
        Theme.StyleList(_connections);
        _connections.BorderStyle = BorderStyle.FixedSingle;
        _connections.DrawSubItem += DrawConnectionState; // runs after StyleList's default handler
        grid.Controls.Add(_connections);
        grid.SetColumnSpan(_connections, 2);

        var limitRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        limitRow.Controls.AddRange([_limitOn, _limit, UiKit.Caption("KB/s", muted: true)]);
        grid.Controls.Add(limitRow);
        grid.SetColumnSpan(limitRow, 2);
        grid.Controls.Add(_closeWhenDone);
        grid.SetColumnSpan(_closeWhenDone, 2);

        _limitOn.Checked = item.SpeedLimit > 0;
        _limit.Value = Math.Clamp(item.SpeedLimit > 0 ? item.SpeedLimit / 1024 : 512, _limit.Minimum, _limit.Maximum);
        _limit.Enabled = _limitOn.Checked;
        _limitOn.CheckedChanged += (_, _) => ApplyLimit();
        _limit.ValueChanged += (_, _) => ApplyLimit();

        _pause = UiKit.Button("Pause", (_, _) => TogglePause());
        _details = UiKit.Button("Show connections", (_, _) => ToggleConnections());
        var stop = UiKit.Button("Stop", async (_, _) =>
        {
            await _item.PauseAsync();
            Close();
        });
        Theme.Tint(stop, ButtonRole.Stop);
        Controls.Add(grid);
        Controls.Add(header);
        Controls.Add(UiKit.ButtonRow(stop, _pause, _details));
        MinimumSize = new Size(LogicalToDeviceUnits(580), 0);

        _timer.Tick += (_, _) => ShowState(_item);
        _timer.Start();
        ShowState(item);
        FormClosed += (_, _) => _timer.Dispose();
    }

    public bool CloseWhenDone => _closeWhenDone.Checked;
    public DownloadItem Item => _item;

    private static Label Value() => new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 4) };

    private void ShowState(DownloadItem d)
    {
        var running = d.IsRunning;
        var pct = Format.Percent(d);
        var title = pct.Length > 0 ? $"{pct} {d.DisplayName}" : d.DisplayName;
        if (Text != title) Text = title;

        _status.Text = d.Status switch
        {
            DownloadStatus.Downloading => $"Downloading with {d.ActiveConnections} of {d.Connections} connections",
            DownloadStatus.Error => $"Error: {d.Error}",
            _ => Format.Status(d),
        };
        _status.ForeColor = Theme.TextForStatus(d.Status);
        _size.Text = Format.Bytes(d.Size);
        _downloaded.Text = d.Fraction is double f ? $"{Format.Bytes(d.Received)}  ({f * 100:0.0}%)" : Format.Bytes(d.Received);
        _speed.Text = d.Status == DownloadStatus.Downloading ? Format.Speed(d.Speed) : "–";
        _eta.Text = d.Status == DownloadStatus.Downloading ? Format.Eta(d.EtaSeconds) : "–";
        var checking = d.Status is DownloadStatus.Queued or DownloadStatus.Connecting && d.Size is null;
        _resume.Text = checking ? "Checking…" : d.Resumable ? "Yes" : "No (one connection, restarts if interrupted)";
        _resume.ForeColor = checking ? Theme.Muted : d.Resumable ? Theme.SuccessText : Theme.WarningText;
        _bar.SetValue(d.Fraction ?? 0, Theme.ForStatus(d.Status));
        _map.Show(d);
        var canPause = running || d.Status == DownloadStatus.Queued;
        _pause.Text = canPause ? "Pause" : "Resume";
        _pause.ForeColor = canPause ? Theme.WarningText : Theme.SuccessText; // yellow pauses, green resumes
        _pause.Enabled = d.Status != DownloadStatus.Completed;
        if (_connections.Visible) RefreshConnections(d);
        if (IsHandleCreated) Taskbar.Set(this, TaskbarFor(d), d.Fraction ?? 0);
    }

    /// <summary>This download on its own taskbar button: green running, yellow paused, red failed.</summary>
    private static TaskbarState TaskbarFor(DownloadItem d) => d.Status switch
    {
        DownloadStatus.Downloading or DownloadStatus.Connecting => d.Size is null ? TaskbarState.Indeterminate : TaskbarState.Normal,
        DownloadStatus.Paused or DownloadStatus.Queued => TaskbarState.Paused,
        DownloadStatus.Error => TaskbarState.Error,
        _ => TaskbarState.None,
    };

    /// <summary>Connection state column: green while receiving, yellow while connecting or waiting to retry.</summary>
    private void DrawConnectionState(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != 3 || e.SubItem is null) return;
        e.DrawDefault = false;
        using (var back = new SolidBrush(e.Item?.Selected == true ? Theme.Selected : _connections.BackColor)) e.Graphics.FillRectangle(back, e.Bounds);
        var color = e.SubItem.Text == "Receiving data" ? Theme.SuccessText : Theme.WarningText;
        TextRenderer.DrawText(e.Graphics, e.SubItem.Text, _connections.Font, Rectangle.Inflate(e.Bounds, -4, 0), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void RefreshConnections(DownloadItem d)
    {
        var rows = d.SnapshotConnections();
        _connections.BeginUpdate();
        while (_connections.Items.Count > rows.Length) _connections.Items.RemoveAt(_connections.Items.Count - 1);
        for (var i = 0; i < rows.Length; i++)
        {
            var c = rows[i];
            string[] cells = [c.Number.ToString(), Format.Bytes(c.Received), Format.Speed(c.Speed), c.State];
            if (i >= _connections.Items.Count) _connections.Items.Add(new ListViewItem(cells));
            else for (var j = 0; j < cells.Length; j++)
            {
                if (_connections.Items[i].SubItems[j].Text != cells[j]) _connections.Items[i].SubItems[j].Text = cells[j];
            }
        }
        // The last column takes the width that is left (after any scrollbar), so there's no horizontal scrolling.
        var others = _connections.Columns.Cast<ColumnHeader>().Take(_connections.Columns.Count - 1).Sum(c => c.Width);
        var last = Math.Max(LogicalToDeviceUnits(80), _connections.ClientSize.Width - others);
        if (_connections.Columns[^1].Width != last) _connections.Columns[^1].Width = last;
        _connections.EndUpdate();
    }

    private void TogglePause()
    {
        if (_item.IsRunning || _item.Status == DownloadStatus.Queued) _ = _item.PauseAsync();
        else _shell.Queue.Resume(_item);
    }

    public void ToggleConnections()
    {
        _connections.Visible = !_connections.Visible;
        _details.Text = _connections.Visible ? "Hide connections" : "Show connections";
        ShowState(_item);
    }

    private void ApplyLimit()
    {
        _limit.Enabled = _limitOn.Checked;
        _item.SetSpeedLimit(_limitOn.Checked ? (long)_limit.Value * 1024 : 0);
    }
}
