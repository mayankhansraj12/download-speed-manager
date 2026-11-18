using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Dsm.Setup;

/// <summary>The app's dark look for the setup wizard: colours, icon glyphs, logo and buttons.</summary>
internal static partial class Look
{
    public static readonly Color Window = Color.FromArgb(32, 32, 32);
    public static readonly Color Sidebar = Color.FromArgb(39, 39, 39);
    public static readonly Color Border = Color.FromArgb(58, 58, 58);
    public static readonly Color Text = Color.FromArgb(240, 240, 240);
    public static readonly Color Muted = Color.FromArgb(160, 160, 160);
    public static readonly Color Accent = Color.FromArgb(76, 145, 255);
    public static readonly Color Success = Color.FromArgb(74, 222, 128);  // green: done, fine
    public static readonly Color Warning = Color.FromArgb(250, 204, 21);  // yellow: attention
    public static readonly Color Danger = Color.FromArgb(248, 113, 113);  // red: failed, destructive
    public static readonly Color Track = Color.FromArgb(62, 62, 62);
    public static readonly Color Input = Color.FromArgb(45, 45, 45);

    public static readonly Color GoFill = Color.FromArgb(21, 128, 61);
    public static readonly Color StopFill = Color.FromArgb(185, 28, 28);
    public static readonly Color NeutralFill = Color.FromArgb(55, 55, 55);

    public const char Check = '';
    public const char Cancel = '';
    public const char Download = '';
    public const char CircleRing = '';
    public const char CircleFill = '';
    public const char Delete = '';

    private static readonly string IconFont = InstalledFont("Segoe Fluent Icons") ?? "Segoe MDL2 Assets";

    private static string? InstalledFont(string name)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == name) ? name : null;
    }

    public static Bitmap Glyph(char glyph, int size, Color color)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(IconFont, size * 0.72f, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, size, size), format);
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
        var d = Math.Max(4, size / 5) * 2;
        using (var path = new GraphicsPath())
        {
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
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

    public static Button Button(string text, Color fill, EventHandler onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(100, 32),
            Padding = new Padding(12, 2, 12, 2),
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
        };
        Paint(button, fill);
        button.Click += onClick;
        return button;
    }

    public static void Paint(Button button, Color fill)
    {
        var neutral = fill == NeutralFill;
        button.BackColor = fill;
        button.ForeColor = neutral ? Text : Color.White;
        button.FlatAppearance.BorderColor = neutral ? Color.FromArgb(78, 78, 78) : fill;
        button.FlatAppearance.MouseOverBackColor = neutral ? Color.FromArgb(66, 66, 66) : ControlPaint.Light(fill, 0.25f);
    }

    public static Label Heading(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Text,
        Font = new Font("Segoe UI Semibold", 15f),
        Margin = new Padding(0, 0, 0, 12),
    };

    public static Label Paragraph(string text, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(440, 0),
        ForeColor = color ?? Text,
        Margin = new Padding(0, 0, 0, 10),
    };

    public static void DarkTitleBar(Form form)
    {
        var on = 1;
        _ = DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}

/// <summary>Flat progress bar in a status colour.</summary>
internal sealed class Meter : Control
{
    private double _value;

    public Meter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 8;
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
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using (var track = new SolidBrush(Look.Track)) e.Graphics.FillRectangle(track, ClientRectangle);
        using var fill = new SolidBrush(Fill);
        e.Graphics.FillRectangle(fill, 0, 0, (int)(Width * _value), Height);
    }
}
