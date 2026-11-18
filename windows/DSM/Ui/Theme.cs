using System.Runtime.InteropServices;
using Dsm.Engine;

namespace Dsm.Ui;

/// <summary>What a button does; Go and Stop buttons are filled green or red in both themes.</summary>
internal enum ButtonRole { Normal, Primary, Go, Stop }

/// <summary>
/// Light and dark colours (Windows 11 style), chosen once at startup. Status colours follow the
/// convention Windows uses for progress: green = running fine or done, yellow = paused or
/// waiting, red = failed or destructive. Blue stays for the brand and selection.
/// </summary>
internal static partial class Theme
{
    public static bool IsDark { get; private set; }

    public static void Use(bool dark) => IsDark = dark;

    private static Color Pick(Color light, Color dark) => IsDark ? dark : light;

    public static Color Window => Pick(Color.White, Color.FromArgb(32, 32, 32));
    public static Color Chrome => Pick(Color.White, Color.FromArgb(43, 43, 43));        // menu, toolbar, list headers
    public static Color Sidebar => Pick(Color.FromArgb(248, 250, 252), Color.FromArgb(39, 39, 39));
    public static Color Border => Pick(Color.FromArgb(226, 232, 240), Color.FromArgb(58, 58, 58));
    public static Color Text => Pick(Color.FromArgb(15, 23, 42), Color.FromArgb(240, 240, 240));
    public static Color Muted => Pick(Color.FromArgb(100, 116, 139), Color.FromArgb(160, 160, 160));
    public static Color Icon => Pick(Color.FromArgb(55, 65, 81), Color.FromArgb(214, 214, 214));
    public static Color Accent => Pick(Color.FromArgb(37, 99, 235), Color.FromArgb(76, 145, 255));
    public static Color Track => Pick(Color.FromArgb(226, 232, 240), Color.FromArgb(62, 62, 62));
    public static Color Input => Pick(Color.White, Color.FromArgb(45, 45, 45));
    public static Color Selected => Pick(Color.FromArgb(226, 232, 240), Color.FromArgb(55, 55, 55));
    public static Color Hover => Pick(Color.FromArgb(224, 236, 255), Color.FromArgb(58, 58, 58));
    public static Color HoverEdge => Pick(Color.FromArgb(147, 197, 253), Color.FromArgb(78, 78, 78));
    public static Color Pressed => Pick(Color.FromArgb(191, 219, 254), Color.FromArgb(68, 68, 68));

    // Status colours: fills (bars, icons) and darker text variants that stay readable on white.
    public static Color Success => Pick(Color.FromArgb(22, 163, 74), Color.FromArgb(74, 222, 128));
    public static Color Warning => Pick(Color.FromArgb(234, 179, 8), Color.FromArgb(250, 204, 21));
    public static Color Danger => Pick(Color.FromArgb(220, 38, 38), Color.FromArgb(248, 113, 113));
    public static Color SuccessText => Pick(Color.FromArgb(21, 128, 61), Color.FromArgb(74, 222, 128));
    public static Color WarningText => Pick(Color.FromArgb(161, 98, 7), Color.FromArgb(250, 204, 21));
    public static Color DangerText => Pick(Color.FromArgb(185, 28, 28), Color.FromArgb(248, 113, 113));

    /// <summary>Bar and icon colour for a download state.</summary>
    public static Color ForStatus(DownloadStatus status) => status switch
    {
        DownloadStatus.Downloading or DownloadStatus.Completed => Success,
        DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Paused => Warning,
        DownloadStatus.Error or DownloadStatus.Cancelled => Danger,
        _ => Muted,
    };

    /// <summary>Text colour for a download state.</summary>
    public static Color TextForStatus(DownloadStatus status) => status switch
    {
        DownloadStatus.Downloading or DownloadStatus.Completed => SuccessText,
        DownloadStatus.Queued or DownloadStatus.Connecting or DownloadStatus.Paused => WarningText,
        DownloadStatus.Error or DownloadStatus.Cancelled => DangerText,
        _ => Text,
    };

    public static readonly ToolStripRenderer Renderer = new ThemedRenderer();

    /// <summary>Mark a button's role; it is coloured when its form loads.</summary>
    public static void Tint(Button button, ButtonRole role) => button.Tag = role;

    /// <summary>Theme a finished form: title bar, inputs, buttons and lists that don't paint themselves.</summary>
    public static void Apply(Form form)
    {
        form.BackColor = Window;
        form.ForeColor = Text;
        form.Load += (_, _) => ApplyToChildren(form);
        if (!IsDark) return;
        if (form.IsHandleCreated) DarkTitleBar(form.Handle);
        form.HandleCreated += (_, _) => DarkTitleBar(form.Handle);
    }

    private static void ApplyToChildren(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            switch (control)
            {
                case Button button:
                    StyleButton(button);
                    break;
                case TextBox textBox when IsDark:
                    textBox.BorderStyle = BorderStyle.FixedSingle; // the 3D border stays bright in dark mode
                    textBox.BackColor = Input;
                    textBox.ForeColor = Text;
                    break;
                case ComboBox or NumericUpDown when IsDark:
                    control.BackColor = Input;
                    control.ForeColor = Text;
                    break;
                case ListView list when IsDark:
                    list.BackColor = Window;
                    list.ForeColor = Text;
                    break;
            }
            ApplyToChildren(control);
        }
    }

    private static void StyleButton(Button button)
    {
        var role = button.Tag is ButtonRole tagged ? tagged
            : button == button.FindForm()?.AcceptButton ? ButtonRole.Primary : ButtonRole.Normal;
        Color? fill = role switch
        {
            ButtonRole.Go => Color.FromArgb(21, 128, 61),   // green, white text has good contrast
            ButtonRole.Stop => Color.FromArgb(185, 28, 28), // red
            ButtonRole.Primary when IsDark => Accent,
            ButtonRole.Normal when IsDark => Color.FromArgb(55, 55, 55),
            _ => null, // light theme keeps the native button look
        };
        if (fill is not Color color) return;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = color;
        button.ForeColor = role == ButtonRole.Normal ? Text : Color.White;
        button.FlatAppearance.BorderColor = role == ButtonRole.Normal ? Color.FromArgb(78, 78, 78) : color;
        button.FlatAppearance.MouseOverBackColor = role == ButtonRole.Normal ? Hover : ControlPaint.Light(color, 0.25f);
    }

    /// <summary>List headers in theme colours (the native header stays light in dark mode).</summary>
    public static void StyleList(ListView list)
    {
        list.OwnerDraw = true;
        list.BackColor = Window;
        list.ForeColor = Text;
        list.BorderStyle = BorderStyle.None;
        list.DrawColumnHeader += (_, e) => DrawHeader(e, list.Font);
        list.DrawItem += (_, _) => { };
        list.DrawSubItem += (_, e) => e.DrawDefault = true;
    }

    public static void DrawHeader(DrawListViewColumnHeaderEventArgs e, Font font)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(Chrome)) g.FillRectangle(back, e.Bounds);
        using (var line = new Pen(Border))
        {
            g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            g.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 5, e.Bounds.Right - 1, e.Bounds.Bottom - 6);
        }
        var align = e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
        TextRenderer.DrawText(g, e.Header?.Text, font, Rectangle.Inflate(e.Bounds, -6, 0), Muted,
            align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static void DarkTitleBar(nint hwnd)
    {
        var on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}

/// <summary>Menus, toolbar and status bar in theme colours. Items can carry a text colour in Tag.</summary>
internal sealed class ThemedRenderer : ToolStripProfessionalRenderer
{
    public ThemedRenderer() : base(new FlatColors()) => RoundedEdges = false;

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Theme.Muted : e.Item.Tag is Color tint ? tint : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            base.OnRenderToolStripBorder(e);
            return;
        }
        using var line = new Pen(Theme.Border);
        var y = e.ToolStrip is StatusStrip ? 0 : e.ToolStrip.Height - 1;
        e.Graphics.DrawLine(line, 0, y, e.ToolStrip.Width, y);
    }
}

internal sealed class FlatColors : ProfessionalColorTable
{
    public override Color ToolStripGradientBegin => Theme.Chrome;
    public override Color ToolStripGradientMiddle => Theme.Chrome;
    public override Color ToolStripGradientEnd => Theme.Chrome;
    public override Color ToolStripBorder => Theme.Border;
    public override Color MenuStripGradientBegin => Theme.Chrome;
    public override Color MenuStripGradientEnd => Theme.Chrome;
    public override Color StatusStripGradientBegin => Theme.Sidebar;
    public override Color StatusStripGradientEnd => Theme.Sidebar;
    public override Color ToolStripDropDownBackground => Theme.Chrome;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemBorder => Theme.HoverEdge;
    public override Color MenuItemSelected => Theme.Hover;
    public override Color MenuItemSelectedGradientBegin => Theme.Hover;
    public override Color MenuItemSelectedGradientEnd => Theme.Hover;
    public override Color MenuItemPressedGradientBegin => Theme.Chrome;
    public override Color MenuItemPressedGradientMiddle => Theme.Chrome;
    public override Color MenuItemPressedGradientEnd => Theme.Chrome;
    public override Color ImageMarginGradientBegin => Theme.Chrome;
    public override Color ImageMarginGradientMiddle => Theme.Chrome;
    public override Color ImageMarginGradientEnd => Theme.Chrome;
    public override Color ButtonSelectedHighlight => Theme.Hover;
    public override Color ButtonSelectedBorder => Theme.HoverEdge;
    public override Color ButtonSelectedGradientBegin => Theme.Hover;
    public override Color ButtonSelectedGradientMiddle => Theme.Hover;
    public override Color ButtonSelectedGradientEnd => Theme.Hover;
    public override Color ButtonPressedBorder => Theme.HoverEdge;
    public override Color ButtonPressedGradientBegin => Theme.Pressed;
    public override Color ButtonPressedGradientMiddle => Theme.Pressed;
    public override Color ButtonPressedGradientEnd => Theme.Pressed;
    public override Color ButtonCheckedGradientBegin => Theme.Pressed;
    public override Color ButtonCheckedGradientMiddle => Theme.Pressed;
    public override Color ButtonCheckedGradientEnd => Theme.Pressed;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Chrome;
}
