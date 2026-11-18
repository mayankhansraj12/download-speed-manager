#if DEBUG
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Dsm.Setup;

/// <summary>Debug builds only: walk through the pages (without installing) and screenshot each one.</summary>
internal sealed partial class SetupForm
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);

    public async Task CaptureAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var prefix = _uninstall ? "uninstall" : "setup";
        var pages = _uninstall ? new[] { 0, -1, 2 } : [0, 1, -1, 3];
        var index = 0;
        foreach (var page in pages)
        {
            if (page == -1)
            {
                ShowStep(_uninstall ? 1 : 2); // progress page, simulated
                _meter.Value = 0.62;
                _status.Text = _uninstall ? "Removing files…" : "Copying files…";
            }
            else
            {
                if (!_uninstall && page == 3) _doneFolder = _folder.Text.Trim();
                ShowStep(page);
            }
            await Task.Delay(500);
            Save(Path.Combine(folder, $"{prefix}-{++index}.png"));
        }
        Close();
    }

    private void Save(string path)
    {
        using var bitmap = new Bitmap(Width, Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            PrintWindow(Handle, hdc, 2); // PW_RENDERFULLCONTENT
            g.ReleaseHdc(hdc);
        }
        bitmap.Save(path, ImageFormat.Png);
    }
}
#endif
