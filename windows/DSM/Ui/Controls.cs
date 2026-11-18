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

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var track = new SolidBrush(Theme.Track)) e.Graphics.FillRectangle(track, ClientRectangle);
        using var fill = new SolidBrush(_color.IsEmpty ? Theme.Success : _color);
        e.Graphics.FillRectangle(fill, 0, 0, (int)(Width * _value), Height);
    }
}

/// <summary>A ListView that repaints without flicker.</summary>
internal sealed class SmoothListView : ListView
{
    public SmoothListView() => DoubleBuffered = true;
}
