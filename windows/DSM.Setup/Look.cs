using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Dsm.Setup;

/// <summary>What a status icon or note means: information, done or fine, needs attention, failed.</summary>
internal enum Tone { Info, Success, Warning, Danger }

/// <summary>The app's dark look for the setup wizard: colours, type, spacing, icons and the controls drawn with them.</summary>
internal static partial class Look
{
    public static readonly Color Window = Color.FromArgb(32, 32, 32);
    public static readonly Color Sidebar = Color.FromArgb(39, 39, 39);
    public static readonly Color Border = Color.FromArgb(58, 58, 58);
    public static readonly Color Text = Color.FromArgb(240, 240, 240);
    public static readonly Color Muted = Color.FromArgb(160, 160, 160);
    public static readonly Color Accent = Color.FromArgb(76, 145, 255);   // blue: brand, selection, focus
    public static readonly Color Success = Color.FromArgb(74, 222, 128);  // green: done, fine
    public static readonly Color Warning = Color.FromArgb(250, 204, 21);  // yellow: attention
    public static readonly Color Danger = Color.FromArgb(248, 113, 113);  // red: failed, destructive
    public static readonly Color Track = Color.FromArgb(62, 62, 62);
    public static readonly Color Input = Color.FromArgb(45, 45, 45);
    public static readonly Color InputFocused = Color.FromArgb(28, 28, 28);
    public static readonly Color InputEdge = Color.FromArgb(70, 70, 70);
    public static readonly Color InputUnderline = Color.FromArgb(120, 120, 120);

    // Button fills; white text keeps at least 4.5:1 contrast on each.
    public static readonly Color AccentFill = Color.FromArgb(37, 99, 235);
    public static readonly Color GoFill = Color.FromArgb(21, 128, 61);
    public static readonly Color StopFill = Color.FromArgb(185, 28, 28);
    public static readonly Color NeutralFill = Color.FromArgb(55, 55, 55);

    // Type ramp. Body text is the app default (Segoe UI 9 pt); point sizes follow the screen's DPI by themselves.
    public static readonly Font TitleFont = new("Segoe UI Semibold", 15f);
    public static readonly Font BrandFont = new("Segoe UI Semibold", 11f);
    public static readonly Font StrongFont = new("Segoe UI Semibold", 9f);

    public const char Download = '';

    private static readonly string IconFont = InstalledFont("Segoe Fluent Icons") ?? "Segoe MDL2 Assets";
    private static float? s_scale;

    /// <summary>Screen pixels per logical pixel. Read on first use, after the app has declared itself DPI aware.</summary>
    private static float DpiScale => s_scale ??= GetDpiForSystem() / 96f;

    /// <summary>Logical pixels (as at 100% scaling) to screen pixels, so spacing grows along with the text.</summary>
    public static int Px(int value) => (int)Math.Round(value * DpiScale);

    public static Size Px(int width, int height) => new(Px(width), Px(height));

    public static Padding Px(int left, int top, int right, int bottom) => new(Px(left), Px(top), Px(right), Px(bottom));

    /// <summary>Width of the text column on every page.</summary>
    public static int ContentWidth => Px(440);

    private static string? InstalledFont(string name)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == name) ? name : null;
    }

    public static Color ToneColor(Tone tone) => tone switch
    {
        Tone.Success => Success,
        Tone.Warning => Warning,
        Tone.Danger => Danger,
        _ => Accent,
    };

    /// <summary><paramref name="amount"/> (0 to 1) of <paramref name="top"/> mixed into <paramref name="bottom"/>.</summary>
    public static Color Blend(Color bottom, Color top, float amount) => Color.FromArgb(
        (int)Math.Round(bottom.R + (top.R - bottom.R) * amount),
        (int)Math.Round(bottom.G + (top.G - bottom.G) * amount),
        (int)Math.Round(bottom.B + (top.B - bottom.B) * amount));

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Anti-aliased drawing where (0, 0) is the corner of the first pixel, so half-pixel lines stay crisp.</summary>
    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    /// <summary>A filled circle in the tone's colour with a dark mark: check, cross, ! or i.</summary>
    public static Bitmap StatusIcon(Tone tone, int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        Smooth(g);
        using (var fill = new SolidBrush(ToneColor(tone))) g.FillEllipse(fill, 0, 0, size, size);
        var stroke = Math.Max(1.5f, size * 0.1f);
        using var pen = new Pen(Window, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var ink = new SolidBrush(Window);
        PointF At(float x, float y) => new(x * size, y * size);
        void Dot(PointF c)
        {
            var d = stroke * 1.2f;
            g.FillEllipse(ink, c.X - d / 2, c.Y - d / 2, d, d);
        }
        switch (tone)
        {
            case Tone.Success:
                g.DrawLines(pen, [At(0.29f, 0.52f), At(0.44f, 0.66f), At(0.71f, 0.37f)]);
                break;
            case Tone.Danger:
                g.DrawLine(pen, At(0.35f, 0.35f), At(0.65f, 0.65f));
                g.DrawLine(pen, At(0.65f, 0.35f), At(0.35f, 0.65f));
                break;
            case Tone.Warning:
                g.DrawLine(pen, At(0.5f, 0.27f), At(0.5f, 0.55f));
                Dot(At(0.5f, 0.73f));
                break;
            default:
                Dot(At(0.5f, 0.28f));
                g.DrawLine(pen, At(0.5f, 0.46f), At(0.5f, 0.73f));
                break;
        }
        return bitmap;
    }

    /// <summary>Step list marker: a ring for steps still to come; the current step's ring is thicker, with a dot.</summary>
    public static Bitmap Ring(int size, Color color, bool current)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        Smooth(g);
        var stroke = Math.Max(1f, size / 14f) * (current ? 1.6f : 1f);
        using (var pen = new Pen(color, stroke))
        {
            var inset = stroke / 2 + 0.5f;
            g.DrawEllipse(pen, inset, inset, size - 2 * inset, size - 2 * inset);
        }
        if (current)
        {
            using var dot = new SolidBrush(color);
            var d = size * 0.42f;
            g.FillEllipse(dot, (size - d) / 2, (size - d) / 2, d, d);
        }
        return bitmap;
    }

    /// <summary>The app logo: a white download arrow on a rounded blue square.</summary>
    public static Bitmap Logo(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var r = new Rectangle(0, 0, size - 1, size - 1);
        using (var path = Rounded(r, Math.Max(4, size / 5)))
        {
            using var fill = new LinearGradientBrush(r, Color.FromArgb(59, 130, 246), Color.FromArgb(29, 78, 216), 90f);
            g.FillPath(fill, path);
        }
        using var font = new Font(IconFont, size * 0.6f, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Download.ToString(), font, Brushes.White, new RectangleF(0, size * 0.03f, size, size), format);
        return bitmap;
    }

    public static Icon AppIcon()
    {
        using var stream = typeof(Look).Assembly.GetManifestResourceStream("app.ico");
        return stream is null ? Icon.FromHandle(Logo(32).GetHicon()) : new Icon(stream);
    }

    public static FlatButton Button(string text, Color fill, EventHandler onClick)
    {
        var button = new FlatButton
        {
            Text = text,
            Fill = fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = Px(100, 32),
            Padding = Px(12, 0, 12, 0),
            Margin = Px(8, 0, 0, 0),
        };
        button.Click += onClick;
        return button;
    }

    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        UseMnemonic = false,
        ForeColor = Text,
        Font = TitleFont,
        Margin = Px(0, 0, 0, 12),
    };

    /// <summary>Body text wrapped to the column. Shows '&amp;' as typed: paths and error messages can contain one.</summary>
    public static Label Paragraph(string text, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        UseMnemonic = false,
        ForeColor = color ?? Text,
        Margin = Px(0, 0, 0, 10),
    };

    public static void DarkTitleBar(Form form)
    {
        var on = 1;
        _ = DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();
}

/// <summary>
/// A push button in the wizard's colours: rounded, with hover, pressed and disabled states, and a ring
/// when it has keyboard focus. Neutral buttons are grey; Accent, Go and Stop buttons are filled with white text.
/// </summary>
internal sealed class FlatButton : Button
{
    private static readonly Color NeutralEdge = Color.FromArgb(78, 78, 78);
    private static readonly Color NeutralHover = Color.FromArgb(66, 66, 66);
    private static readonly Color NeutralPressed = Color.FromArgb(46, 46, 46);
    private static readonly Color DisabledFill = Color.FromArgb(42, 42, 42);
    private static readonly Color DisabledEdge = Color.FromArgb(54, 54, 54);
    private static readonly Color DisabledText = Color.FromArgb(115, 115, 115);

    private Color _fill = Look.NeutralFill;
    private bool _hot;
    private bool _down;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        UseVisualStyleBackColor = false;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill
    {
        get => _fill;
        set
        {
            _fill = value;
            BackColor = value;
            Invalidate();
        }
    }

    private void SetState(bool hot, bool down)
    {
        if (hot == _hot && down == _down) return;
        _hot = hot;
        _down = down;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        SetState(true, _down);
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        SetState(false, _down);
        base.OnMouseLeave(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        SetState(ClientRectangle.Contains(e.Location), _down);
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) SetState(true, true);
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        SetState(ClientRectangle.Contains(e.Location), false);
        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) SetState(true, true);
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) SetState(ClientRectangle.Contains(PointToClient(MousePosition)), false);
        base.OnKeyUp(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        SetState(_hot, false);
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        SetState(false, false);
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnChangeUICues(UICuesEventArgs e)
    {
        Invalidate();
        base.OnChangeUICues(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Everything is drawn in OnPaint.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Look.Window);
        var neutral = _fill == Look.NeutralFill;
        var pressed = _down && _hot;
        Color fill, edge, text;
        if (!Enabled) (fill, edge, text) = (DisabledFill, DisabledEdge, DisabledText);
        else if (neutral) (fill, edge, text) = (pressed ? NeutralPressed : _hot ? NeutralHover : _fill, NeutralEdge, pressed ? Look.Muted : Look.Text);
        else
        {
            fill = pressed ? Look.Blend(_fill, Color.Black, 0.18f) : _hot ? Look.Blend(_fill, Color.White, 0.1f) : _fill;
            (edge, text) = (fill, pressed ? Color.FromArgb(225, 225, 225) : Color.White);
        }

        Look.Smooth(g);
        var radius = Look.Px(4);
        using (var path = Look.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radius))
        {
            using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
            using (var pen = new Pen(edge)) g.DrawPath(pen, path);
        }
        if (Focused && ShowFocusCues)
        {
            var ring = Math.Max(2, Look.Px(2));
            using var path = Look.Rounded(new RectangleF(ring / 2f, ring / 2f, Width - ring, Height - ring), radius - ring / 4f);
            using var pen = new Pen(Look.Text, ring);
            g.DrawPath(pen, path);
        }

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
        if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, flags);
    }
}

/// <summary>A single-line text box drawn to match the buttons: same height, rounded edge, accent underline while focused.</summary>
internal sealed class Field : Panel
{
    private readonly TextBox _box;
    private bool _invalid;

    public Field(TextBox box)
    {
        _box = box;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        box.BorderStyle = BorderStyle.None;
        box.ForeColor = Look.Text;
        box.GotFocus += (_, _) => Restyle();
        box.LostFocus += (_, _) => Restyle();
        Controls.Add(box);
        Height = Look.Px(32);
        Cursor = Cursors.IBeam;
        Restyle();
    }

    /// <summary>Shows the field in red until the text is fixed.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Invalid
    {
        get => _invalid;
        set
        {
            _invalid = value;
            Invalidate();
        }
    }

    private Color Fill => _box.Focused ? Look.InputFocused : Look.Input;

    private void Restyle()
    {
        _box.BackColor = Fill;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _box.Focus(); // a click on the padding around the text still puts the caret in it
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var inset = Look.Px(10);
        _box.SetBounds(inset, (Height - _box.Height) / 2, Width - 2 * inset, _box.Height);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Everything is drawn in OnPaint.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Look.Window);
        Look.Smooth(g);
        var radius = Look.Px(4);
        var focused = _box.Focused;
        using (var path = Look.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), radius))
        {
            using (var fill = new SolidBrush(Fill)) g.FillPath(fill, path);
            using (var edge = new Pen(_invalid ? Look.Danger : Look.InputEdge)) g.DrawPath(edge, path);
        }
        // Underline: thin and light at rest, thicker in the accent colour (red if invalid) while typing.
        var line = focused ? Look.Px(2) : 1;
        using var under = new SolidBrush(_invalid ? Look.Danger : focused ? Look.Accent : Look.InputUnderline);
        g.FillRectangle(under, radius, Height - line, Width - 2 * radius, line);
    }
}

/// <summary>A tinted note with a status icon: something to know, or to do, before going on.</summary>
internal sealed class Notice : FlowLayoutPanel
{
    private readonly Color _edge;

    public Notice(Tone tone, string text)
    {
        var color = Look.ToneColor(tone);
        _edge = Look.Blend(Look.Window, color, 0.32f);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = true;
        WrapContents = false;
        BackColor = Look.Blend(Look.Window, color, 0.12f);
        Padding = Look.Px(12, 10, 12, 10);
        Margin = Look.Px(0, 2, 0, 12);
        MinimumSize = new Size(Look.ContentWidth, 0);
        var icon = Look.Px(16);
        var gap = Look.Px(10);
        Controls.Add(new PictureBox
        {
            Image = Look.StatusIcon(tone, icon),
            Size = new Size(icon, icon),
            Margin = new Padding(0, 0, gap, 0),
            AccessibleRole = AccessibleRole.Graphic,
            AccessibleName = tone switch { Tone.Info => "Information", Tone.Success => "Note", Tone.Warning => "Warning", _ => "Error" },
        });
        Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            UseMnemonic = false,
            ForeColor = Look.Text,
            MaximumSize = new Size(Look.ContentWidth - Padding.Horizontal - icon - gap, 0),
            Margin = Padding.Empty,
        });
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Look.Window);
        Look.Smooth(g);
        using var path = Look.Rounded(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Look.Px(4));
        using (var fill = new SolidBrush(BackColor)) g.FillPath(fill, path);
        using (var edge = new Pen(_edge)) g.DrawPath(edge, path);
    }
}

/// <summary>Flat, rounded progress bar in a status colour.</summary>
internal sealed class Meter : Control
{
    private double _value;

    public Meter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = Look.Px(6);
        AccessibleName = "Progress";
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = Look.Success;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 1);
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Look.Window);
        Look.Smooth(g);
        var radius = Height / 2f;
        using (var track = new SolidBrush(Look.Track))
        using (var path = Look.Rounded(new RectangleF(0, 0, Width, Height), radius))
            g.FillPath(track, path);
        if (_value <= 0) return;
        using var fill = new SolidBrush(Fill);
        using var bar = Look.Rounded(new RectangleF(0, 0, Math.Max(Height, (float)(Width * _value)), Height), radius);
        g.FillPath(fill, bar);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new MeterAccessibleObject(this);

    /// <summary>Lets screen readers announce it as a progress bar with its percentage.</summary>
    private sealed class MeterAccessibleObject : ControlAccessibleObject
    {
        private readonly Meter _meter;

        public MeterAccessibleObject(Meter meter) : base(meter) => _meter = meter;

        public override AccessibleRole Role => AccessibleRole.ProgressBar;

        public override string? Value => $"{Math.Round(_meter._value * 100)}%";
    }
}
