namespace Dsm.Ui;

/// <summary>Small helpers for code-built dialogs that size themselves (DPI-safe).</summary>
internal static class UiKit
{
    public static TableLayoutPanel Grid(params float[] columnPercents)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = columnPercents.Length,
            Padding = new Padding(16, 14, 16, 6),
        };
        foreach (var percent in columnPercents)
            grid.ColumnStyles.Add(percent > 0 ? new ColumnStyle(SizeType.Percent, percent) : new ColumnStyle(SizeType.AutoSize));
        return grid;
    }

    public static Label Caption(string text, bool muted = false) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 7, 12, 7),
        ForeColor = muted ? Theme.Muted : Theme.Text,
    };

    public static Button Button(string text, EventHandler? onClick = null)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(92, 30),
            Padding = new Padding(10, 1, 10, 1),
            Margin = new Padding(6, 0, 0, 0),
            UseVisualStyleBackColor = true,
        };
        if (onClick is not null) button.Click += onClick;
        return button;
    }

    public static FlowLayoutPanel ButtonRow(params Control[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 6, 16, 14),
        };
        row.Controls.AddRange(buttons);
        return row;
    }

    /// <summary>Common settings for a fixed-size, self-sizing dialog.</summary>
    public static void Dialog(Form form, string title)
    {
        form.Text = title;
        form.AutoScaleMode = AutoScaleMode.Font;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.AutoSize = true;
        form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        form.StartPosition = FormStartPosition.CenterScreen;
        Theme.Apply(form);
        form.ShowInTaskbar = true;
    }
}
