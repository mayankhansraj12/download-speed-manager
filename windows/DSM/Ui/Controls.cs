using System.Runtime.InteropServices;
using Dsm.Engine;

namespace Dsm.Ui;

/// <summary>Map of the file: each segment fills from its start offset, with thin gaps between segments.</summary>
internal sealed class SegmentBar : Control
{
    private static Color Track => Theme.Track;
    private static Color Edge => Theme.Border;
    private SegmentState[] _segments = [];
    private long? _size;
    private long _received;
    private bool _complete;
    private DownloadStatus _status;

    public SegmentBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 18;
    }

    public void Show(DownloadItem d)
    {
        _segments = d.SnapshotSegments();
        _size = d.Size;
        _received = d.Received;
        _complete = d.Status == DownloadStatus.Completed;
        _status = d.Status;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        using (var track = new SolidBrush(Track)) g.FillRectangle(track, r);
        using var fill = new SolidBrush(Theme.ForStatus(_status)); // green running/done, yellow paused, red failed
        if (_complete)
        {
            g.FillRectangle(fill, r);
        }
        else if (_size is long size && size > 0)
        {
            float X(long offset) => (float)(r.Width * (double)offset / size);
            if (_segments.Length == 0)
            {
                g.FillRectangle(fill, 0, 0, X(Math.Min(_received, size)), r.Height);
            }
            else
            {
                foreach (var s in _segments.Where(s => s.Received > 0))
                    g.FillRectangle(fill, X(s.Start), 0, Math.Max(1f, X(Math.Min(s.Received, s.End - s.Start + 1))), r.Height);
                using var gap = new Pen(BackColor);
                foreach (var s in _segments.Where(s => s.Start > 0)) g.DrawLine(gap, X(s.Start), 0, X(s.Start), r.Height);
            }
        }
        using var border = new Pen(Edge);
        g.DrawRectangle(border, 0, 0, r.Width - 1, r.Height - 1);
    }
}

/// <summary>Overall progress in theme colours (the native progress bar stays light in dark mode).</summary>
internal sealed class Meter : Control
{
    private double _value;
    private Color _color = Color.Empty;

    public Meter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 8;
    }

    public void SetValue(double fraction, Color color)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (Math.Abs(fraction - _value) < 0.0005 && color == _color) return;
        _value = fraction;
        _color = color;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) =>
        Theme.DrawBar(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), _value, _color.IsEmpty ? Theme.Success : _color, Theme.Track);
}

/// <summary>The sidebar tree: repaints without flicker and tells its owner when to draw keyboard focus.</summary>
internal sealed class NavTree : TreeView
{
    public NavTree() => DoubleBuffered = true;

    /// <summary>True when keyboard focus cues should be drawn (the user has used the keyboard).</summary>
    public bool FocusCuesVisible => ShowFocusCues;
}

/// <summary>A ListView that repaints without flicker and can show a message while it has no rows.</summary>
internal sealed partial class SmoothListView : ListView
{
    private const int WmPaint = 0x000F;
    private const int LvmGetHeader = 0x101F;
    private (char Glyph, string Title, string Hint)? _empty;
    private Font? _titleFont;

    public SmoothListView() => DoubleBuffered = true;

    /// <summary>True when keyboard focus cues should be drawn (the user has used the keyboard).</summary>
    public bool FocusCuesVisible => ShowFocusCues;

    /// <summary>Icon, heading and hint drawn in the middle of the list while it is empty.</summary>
    public void SetEmptyMessage(char glyph, string title, string hint)
    {
        if (_empty == (glyph, title, hint)) return;
        _empty = (glyph, title, hint);
        if (Items.Count == 0) Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmPaint && View == View.Details) PaintBelowRows();
    }

    /// <summary>
    /// The space under the last row: the dark theme draws column dividers there (the light theme
    /// doesn't), so it is cleared to the list colour; while the list is empty it shows the message
    /// (on a cleared background, so partial repaints don't draw the text twice).
    /// </summary>
    private void PaintBelowRows()
    {
        var top = HeaderHeight();
        var rowsEnd = Items.Count == 0 ? top : Math.Max(top, Items[Items.Count - 1].Bounds.Bottom);
        var empty = Items.Count == 0 ? _empty : null;
        if (rowsEnd >= ClientSize.Height || (!Theme.IsDark && empty is null)) return;
        using var g = CreateGraphics();
        using (var back = new SolidBrush(BackColor)) g.FillRectangle(back, 0, rowsEnd, ClientSize.Width, ClientSize.Height - rowsEnd);
        if (empty is { } message) PaintEmpty(g, message, top);
    }

    private void PaintEmpty(Graphics g, (char Glyph, string Title, string Hint) empty, int top)
    {
        var area = new Rectangle(0, top, ClientSize.Width, ClientSize.Height - top);
        var textWidth = Math.Min(area.Width - LogicalToDeviceUnits(32), LogicalToDeviceUnits(400));
        if (textWidth <= 0 || area.Height <= 0) return;
        _titleFont ??= new Font(Font.FontFamily, 11f, FontStyle.Bold);
        const TextFormatFlags Flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        var icon = Glyphs.Render(empty.Glyph, LogicalToDeviceUnits(40), Theme.Blend(Theme.Window, Theme.Muted, 0.75f));
        var gap = LogicalToDeviceUnits(10);
        var titleSize = TextRenderer.MeasureText(g, empty.Title, _titleFont, new Size(textWidth, 0), Flags);
        var hintSize = TextRenderer.MeasureText(g, empty.Hint, Font, new Size(textWidth, 0), Flags);
        var height = icon.Height + gap + titleSize.Height + LogicalToDeviceUnits(4) + hintSize.Height;
        var y = area.Top + Math.Max(LogicalToDeviceUnits(12), (area.Height - height) * 2 / 5); // a little above the middle
        var x = area.Left + (area.Width - textWidth) / 2;
        g.DrawImage(icon, area.Left + (area.Width - icon.Width) / 2, y);
        y += icon.Height + gap;
        TextRenderer.DrawText(g, empty.Title, _titleFont, new Rectangle(x, y, textWidth, titleSize.Height), Theme.Text, Flags);
        y += titleSize.Height + LogicalToDeviceUnits(4);
        TextRenderer.DrawText(g, empty.Hint, Font, new Rectangle(x, y, textWidth, hintSize.Height), Theme.Muted, Flags);
    }

    private int HeaderHeight()
    {
        if (View != View.Details || HeaderStyle == ColumnHeaderStyle.None) return 0;
        var header = SendMessage(Handle, LvmGetHeader, 0, 0);
        return header != 0 && GetWindowRect(header, out var r) ? r.Bottom - r.Top : 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _titleFont?.Dispose();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);
}

/// <summary>
/// Search field for the toolbar: a rounded box in theme colours with a search icon, a placeholder,
/// a clear button, Esc to clear and Enter to move to the results.
/// </summary>
internal sealed class SearchBox : Control
{
    private readonly TextBox _box = new() { BorderStyle = BorderStyle.None };
    private readonly string _placeholder;
    private readonly string _shortcut;
    private bool _hot;
    private bool _clearHot;

    /// <summary>The placeholder shows the shortcut too, e.g. "Search downloads (Ctrl+F)", when there is room.</summary>
    public SearchBox(string placeholder, string shortcut)
    {
        _placeholder = placeholder;
        _shortcut = shortcut;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false); // keyboard focus goes to the text box inside
        TabStop = false;
        BackColor = Theme.Chrome;
        Cursor = Cursors.IBeam;
        _box.BackColor = Theme.Input;
        _box.ForeColor = Theme.Text;
        _box.PlaceholderText = placeholder;
        _box.AccessibleName = $"{placeholder} ({shortcut})";
        _box.TextChanged += (_, _) =>
        {
            Invalidate();
            OnTextChanged(EventArgs.Empty);
        };
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        _box.MouseEnter += (_, _) => SetHot(true, false);
        _box.MouseLeave += (_, _) => SetHot(ClientRectangle.Contains(PointToClient(MousePosition)), _clearHot);
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _box.TextLength > 0) _box.Clear();
            else if (e.KeyCode is Keys.Enter or Keys.Down or Keys.Escape) ResultsRequested?.Invoke(this, EventArgs.Empty);
            else return;
            e.Handled = e.SuppressKeyPress = true; // no beep on Enter
        };
        Controls.Add(_box);
    }

    /// <summary>Enter, Down, or Esc on an empty box: the user is done typing and wants to go to the list.</summary>
    public event EventHandler? ResultsRequested;

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    public void FocusText()
    {
        _box.Focus();
        _box.SelectAll();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _box.Focus();
    }

    private Rectangle ClearButton => new(Width - Height, 0, Height, Height);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var left = LogicalToDeviceUnits(32);
        _box.SetBounds(left, (Height - _box.Height) / 2, Math.Max(10, Width - left - Height), _box.Height);
        var full = $"{_placeholder} ({_shortcut})";
        var placeholder = TextRenderer.MeasureText(full, _box.Font).Width <= _box.Width - LogicalToDeviceUnits(4) ? full : _placeholder;
        if (_box.PlaceholderText != placeholder) _box.PlaceholderText = placeholder;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var radius = LogicalToDeviceUnits(4);
        var frame = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Glyphs.RoundedRect(frame, radius))
        {
            using (var fill = new SolidBrush(Theme.Input)) g.FillPath(fill, path);
            using var border = new Pen(_hot && !_box.Focused ? Theme.InputEdge : Theme.InputBorder);
            g.DrawPath(border, path);
        }
        // Bottom edge as in Windows 11 text fields: a thin line, or a thicker accent line while typing.
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        var focused = _box.Focused;
        var thickness = focused ? LogicalToDeviceUnits(2) : 1;
        using (var edge = new SolidBrush(focused ? Theme.Accent : Theme.InputEdge))
            g.FillRectangle(edge, radius, Height - thickness, Width - 2 * radius, thickness);

        var icon = Glyphs.Render(Glyphs.Search, LogicalToDeviceUnits(16), Theme.Muted);
        g.DrawImage(icon, LogicalToDeviceUnits(10), (Height - icon.Height) / 2);
        if (_box.TextLength > 0)
        {
            var clear = ClearButton;
            if (_clearHot)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var path = Glyphs.RoundedRect(Rectangle.Inflate(clear, -LogicalToDeviceUnits(4), -LogicalToDeviceUnits(4)), radius);
                using var hot = new SolidBrush(Theme.Selected);
                g.FillPath(hot, path);
            }
            var x = Glyphs.Render(Glyphs.Cancel, LogicalToDeviceUnits(12), _clearHot ? Theme.Text : Theme.Muted);
            g.DrawImage(x, clear.X + (clear.Width - x.Width) / 2, (Height - x.Height) / 2);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var overClear = _box.TextLength > 0 && ClearButton.Contains(e.Location);
        Cursor = overClear ? Cursors.Default : Cursors.IBeam;
        SetHot(true, overClear);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHot(_box.ClientRectangle.Contains(_box.PointToClient(MousePosition)), false);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_box.TextLength > 0 && ClearButton.Contains(e.Location)) _box.Clear();
        _box.Focus();
    }

    private void SetHot(bool hot, bool clearHot)
    {
        if (_hot == hot && _clearHot == clearHot) return;
        _hot = hot;
        _clearHot = clearHot;
        Invalidate();
    }
}
