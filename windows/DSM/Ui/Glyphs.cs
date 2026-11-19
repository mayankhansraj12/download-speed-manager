using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Dsm.Ui;

/// <summary>Toolbar and tree icons drawn from the Windows icon font, plus the app's own logo.</summary>
internal static class Glyphs
{
    public const char Add = '';
    public const char Download = '';
    public const char Play = '';
    public const char Pause = '';
    public const char Stop = '';
    public const char Delete = '';
    public const char Clean = '';
    public const char Settings = '';
    public const char Folder = '';
    public const char OpenFile = '';
    public const char Speed = '';
    public const char Video = '';
    public const char Audio = '';
    public const char Document = '';
    public const char Archive = '';
    public const char Software = '';
    public const char Image = '';
    public const char Other = '';
    public const char All = '';
    public const char Clock = '';
    public const char Check = '';
    public const char Link = '';
    public const char Copy = '';
    public const char Info = '';
    public const char Search = '';
    public const char Cancel = '';

    private static readonly Dictionary<(char, int, int), Bitmap> Cache = [];
    private static readonly string FontName = InstalledFont("Segoe Fluent Icons") ?? "Segoe MDL2 Assets";

    public static Color Accent => Theme.Accent;
    public static Color Ink => Theme.Icon;

    private static string? InstalledFont(string name)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == name) ? name : null;
    }

    public static char ForCategory(string category) => category switch
    {
        "Video" => Video,
        "Audio" => Audio,
        "Documents" => Document,
        "Archives" => Archive,
        "Software" => Software,
        "Images" => Image,
        _ => Other,
    };

    public static Bitmap Render(char glyph, int size, Color? color = null)
    {
        var tint = color ?? Ink;
        var key = (glyph, size, tint.ToArgb());
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            using (var font = new Font(FontName, size * 0.72f, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(tint))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, size, size), format);
            }
            Cache[key] = bitmap;
            return bitmap;
        }
    }

    /// <summary>The app logo: a white download arrow on a rounded blue square.</summary>
    public static Bitmap Logo(int size)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var radius = Math.Max(2, size / 5);
        var rect = new Rectangle(0, 0, size - 1, size - 1);
        using (var path = RoundedRect(rect, radius))
        using (var fill = new LinearGradientBrush(rect, Color.FromArgb(59, 130, 246), Color.FromArgb(29, 78, 216), 90f))
        {
            g.FillPath(fill, path);
        }
        using var font = new Font(FontName, size * 0.6f, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Download.ToString(), font, Brushes.White, new RectangleF(0, size * 0.03f, size, size), format);
        return bitmap;
    }

    public static Icon LogoIcon(int size) => Icon.FromHandle(Logo(size).GetHicon());

    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
