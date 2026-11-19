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
    public static Color Muted => Pick(Color.FromArgb(91, 107, 127), Color.FromArgb(160, 160, 160)); // 4.5:1 or more on tinted rows too
    public static Color Icon => Pick(Color.FromArgb(55, 65, 81), Color.FromArgb(214, 214, 214));
    public static Color Accent => Pick(Color.FromArgb(37, 99, 235), Color.FromArgb(76, 145, 255));
    public static Color Track => Pick(Color.FromArgb(226, 232, 240), Color.FromArgb(62, 62, 62));
    public static Color Input => Pick(Color.White, Color.FromArgb(45, 45, 45));
    public static Color InputBorder => Pick(Color.FromArgb(203, 213, 225), Color.FromArgb(66, 66, 66));
    public static Color InputEdge => Pick(Color.FromArgb(148, 163, 184), Color.FromArgb(118, 118, 118)); // bottom edge of a text field
    public static Color Selected => Pick(Color.FromArgb(226, 232, 240), Color.FromArgb(55, 55, 55));
    public static Color Hover => Pick(Color.FromArgb(224, 236, 255), Color.FromArgb(58, 58, 58));
    public static Color HoverEdge => Pick(Color.FromArgb(147, 197, 253), Color.FromArgb(78, 78, 78));
    public static Color Pressed => Pick(Color.FromArgb(191, 219, 254), Color.FromArgb(68, 68, 68));

    // List rows: a light tint on hover, an accent tint for the selection while the list has focus
    // (Selected when it doesn't). Text keeps its normal colours on all of them.
    public static Color RowHover => Pick(Color.FromArgb(241, 245, 249), Color.FromArgb(45, 45, 45));
    public static Color RowSelected => Pick(Color.FromArgb(219, 234, 254), Color.FromArgb(42, 58, 84));
    public static Color NavHover => Blend(Sidebar, Selected, 0.5f); // hovered item in a navigation pane

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

    /// <summary>Mix two colours: 0 gives <paramref name="from"/>, 1 gives <paramref name="to"/>.</summary>
    public static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    /// <summary>Background of a list row in its current state.</summary>
    public static Color RowBack(bool selected, bool listFocused, bool hot, Color normal) =>
        selected ? (listFocused ? RowSelected : Selected) : hot ? RowHover : normal;

    /// <summary>Empty part of a progress bar drawn on <paramref name="back"/> (visible on every row state).</summary>
    public static Color TrackOn(Color back) => Blend(back, Text, IsDark ? 0.14f : 0.12f);

    /// <summary>A pill-shaped progress bar: the track, then the filled part.</summary>
    public static void DrawBar(Graphics g, Rectangle bar, double fraction, Color fill, Color track)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var radius = bar.Height / 2;
        using (var path = Glyphs.RoundedRect(bar, radius))
        using (var brush = new SolidBrush(track))
        {
            g.FillPath(brush, path);
        }
        var width = (int)Math.Round(bar.Width * Math.Clamp(fraction, 0, 1));
        if (width > 0)
        {
            using var path = Glyphs.RoundedRect(new Rectangle(bar.X, bar.Y, Math.Max(width, bar.Height), bar.Height), radius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }
        g.SmoothingMode = mode;
    }

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
            if (control is SearchBox) continue; // draws its own frame around a borderless text box
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
        if (role == ButtonRole.Normal && !IsDark) return; // light theme keeps the native button look
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        Recolor(button, role);
        button.EnabledChanged += (_, _) => Recolor(button, role);
        button.Paint += (_, e) =>
        {
            // Flat buttons draw disabled text in a darker shade of the fill, which is unreadable on dark fills.
            if (button.Enabled) return;
            using (var back = new SolidBrush(button.BackColor)) e.Graphics.FillRectangle(back, Rectangle.Inflate(button.ClientRectangle, -1, -1));
            TextRenderer.DrawText(e.Graphics, button.Text, button.Font, button.ClientRectangle, Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.HidePrefix);
        };
    }

    /// <summary>Filled buttons (white text has at least 4.5:1 contrast on every fill), with hover, pressed and disabled looks.</summary>
    private static void Recolor(Button button, ButtonRole role)
    {
        var normal = role == ButtonRole.Normal;
        var fill = role switch
        {
            ButtonRole.Go => Color.FromArgb(21, 128, 61),    // green
            ButtonRole.Stop => Color.FromArgb(185, 28, 28),  // red
            ButtonRole.Primary => Color.FromArgb(37, 99, 235), // blue
            _ => Color.FromArgb(55, 55, 55),                 // dark theme only
        };
        if (!button.Enabled)
        {
            fill = Pick(Color.FromArgb(241, 245, 249), Color.FromArgb(45, 45, 45));
            button.BackColor = fill;
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = button.FlatAppearance.MouseDownBackColor = fill;
            return;
        }
        button.BackColor = fill;
        if (!normal) button.ForeColor = Color.White; // Normal buttons keep their text colour (Pause/Resume tint theirs)
        button.FlatAppearance.BorderColor = normal ? Color.FromArgb(78, 78, 78) : fill;
        // Filled buttons darken on hover and press, so white text keeps its contrast.
        button.FlatAppearance.MouseOverBackColor = normal ? Color.FromArgb(64, 64, 64) : Blend(fill, Color.Black, 0.12f);
        button.FlatAppearance.MouseDownBackColor = normal ? Color.FromArgb(48, 48, 48) : Blend(fill, Color.Black, 0.24f);
    }

    /// <summary>
    /// A details list drawn entirely in theme colours: header, rows with hover and selection, and
    /// text aligned like its column. <paramref name="cellColor"/> can colour single cells.
    /// </summary>
    public static void StyleList(ListView list, Func<ListViewItem, int, Color?>? cellColor = null)
    {
        list.OwnerDraw = true;
        list.BackColor = Window;
        list.ForeColor = Text;
        list.BorderStyle = BorderStyle.None;
        list.DrawColumnHeader += (_, e) => DrawHeader(e, list.Font);
        list.DrawItem += (_, _) => { };
        list.DrawSubItem += (_, e) =>
        {
            if (e.Item is not { } item) return;
            var back = RowBack(item.Selected, list.Focused, hot: false, list.BackColor);
            using (var brush = new SolidBrush(back)) e.Graphics.FillRectangle(brush, e.Bounds);
            var align = list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text, list.Font, Rectangle.Inflate(e.Bounds, -list.LogicalToDeviceUnits(6), 0),
                cellColor?.Invoke(item, e.ColumnIndex) ?? Text,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        };
        list.ColumnWidthChanged += (_, _) => list.Invalidate(); // owner-drawn cells depend on the column width
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
        var pad = e.Header?.ListView?.LogicalToDeviceUnits(6) ?? 6; // same inset as the cells below
        TextRenderer.DrawText(g, e.Header?.Text, font, Rectangle.Inflate(e.Bounds, -pad, 0), Muted,
            align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
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
        e.ArrowColor = e.Item?.Enabled == false ? Theme.Muted : Theme.Text;
        base.OnRenderArrow(e);
    }

    // Toolbar buttons and menu items get a rounded fill on hover and press, without a border.
    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown) base.OnRenderButtonBackground(e);
        else FillHighlight(e.Graphics, e.Item, new Rectangle(Point.Empty, e.Item.Size), e.Item is ToolStripButton { Checked: true });
    }

    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown) base.OnRenderDropDownButtonBackground(e);
        else FillHighlight(e.Graphics, e.Item, new Rectangle(Point.Empty, e.Item.Size), false);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var area = e.ToolStrip is ToolStripDropDown
            ? new Rectangle(4, 0, e.Item.Width - 8, e.Item.Height)  // inset from the menu's edges
            : new Rectangle(0, 2, e.Item.Width, e.Item.Height - 4);
        FillHighlight(e.Graphics, e.Item, area, false);
    }

    private static void FillHighlight(Graphics g, ToolStripItem item, Rectangle area, bool isChecked)
    {
        var pressed = item.Pressed || isChecked;
        if (!(pressed || item.Selected)) return;
        var menu = item is ToolStripMenuItem;
        if (!item.Enabled && !menu) return; // menus still show where the keyboard is on a disabled item
        var radius = item.Owner?.LogicalToDeviceUnits(4) ?? 4;
        area.Inflate(-1, -1);
        var mode = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var path = Glyphs.RoundedRect(area, radius))
        using (var fill = new SolidBrush(pressed && !menu ? Theme.Pressed : Theme.Hover))
        {
            g.FillPath(fill, path);
        }
        g.SmoothingMode = mode;
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
