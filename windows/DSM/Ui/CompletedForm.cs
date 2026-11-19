using Dsm.Engine;

namespace Dsm.Ui;

/// <summary>Shown when a download finishes: open the file or its folder.</summary>
internal sealed class CompletedForm : Form
{
    public CompletedForm(Shell shell, DownloadItem item)
    {
        UiKit.Dialog(this, "Download complete");
        Icon = shell.AppIcon;

        var grid = UiKit.Grid(0, 100);
        var icon = new PictureBox
        {
            Image = Glyphs.Render(Glyphs.Check, LogicalToDeviceUnits(36), Theme.Success),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Size = new Size(LogicalToDeviceUnits(48), LogicalToDeviceUnits(48)),
            Margin = new Padding(0, 0, 12, 0),
        };
        var heading = new Label
        {
            Text = item.DisplayName,
            AutoSize = true,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
            MaximumSize = new Size(LogicalToDeviceUnits(420), 0),
        };
        var details = UiKit.Caption($"{Format.Bytes(item.Size ?? item.Received)} saved to {item.Folder}", muted: true);
        details.MaximumSize = new Size(LogicalToDeviceUnits(420), 0);
        var dontShow = new CheckBox { Text = "Don't show this again", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
        grid.Controls.Add(icon, 0, 0);
        grid.SetRowSpan(icon, 3);
        grid.Controls.Add(heading, 1, 0);
        grid.Controls.Add(details, 1, 1);
        grid.Controls.Add(dontShow, 1, 2);

        var open = UiKit.Button("Open", (_, _) =>
        {
            if (item.TargetPath is { } path) Shell.Open(path);
            Close();
        });
        Theme.Tint(open, ButtonRole.Go);
        var folder = UiKit.Button("Open folder", (_, _) =>
        {
            Shell.Reveal(item);
            Close();
        });
        var close = UiKit.Button("Close", (_, _) => Close());
        var buttons = UiKit.ButtonRow(close, folder, open);
        Controls.Add(grid);
        Controls.Add(buttons);
        // The dialog sizes itself to its text; with a short name and folder it must still fit the buttons.
        MinimumSize = SizeFromClientSize(new Size(buttons.GetPreferredSize(Size.Empty).Width, 0));
        AcceptButton = open;
        CancelButton = close;
        ActiveControl = open; // focus the main action, not the check box

        Shown += (_, _) =>
        {
            TopMost = true;
            Activate();
            TopMost = false;
        };
        FormClosed += (_, _) =>
        {
            if (!dontShow.Checked) return;
            shell.Settings.ShowCompletionDialog = false;
            shell.ApplySettings();
        };
    }
}
