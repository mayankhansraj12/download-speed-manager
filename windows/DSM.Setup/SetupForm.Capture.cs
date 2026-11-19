#if DEBUG
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Dsm.Setup;

/// <summary>Debug builds only: walk through the pages (without installing) and screenshot each one.</summary>
internal sealed partial class SetupForm
{
    private const int Working = -1; // the progress page, simulated
    private const int Failed = -2;  // the last page after an error, simulated

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint hwnd, nint hdc, uint flags);

    /// <summary>Writes setup-1.png … (or uninstall-1.png …): every page in order, then the error page.</summary>
    public async Task CaptureAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var prefix = _uninstall ? "uninstall" : "setup";
        var last = _steps.Length - 1;
        var pages = _uninstall ? new[] { 0, Working, last, Failed } : [0, 1, Working, last, Failed];
        var index = 0;
        foreach (var page in pages)
        {
            _busy = page == Working; // as in a real run: the buttons are disabled while it works
            if (page == Working)
            {
                ShowStep(last - 1);
                _meter.Value = 0.62;
                _status.Text = _uninstall ? "Removing files…" : "Copying files…";
            }
            else if (page == Failed)
            {
                var file = Path.Combine(_options.Folder ?? Installer.DefaultFolder, _uninstall ? Installer.UninstallerExe : Installer.AppExe);
                _error = $"The process cannot access the file '{file}' because it is being used by another process.";
                ShowStep(last);
            }
            else
            {
                if (!_uninstall && page == last) _doneFolder = _folder.Text.Trim();
                ShowStep(page);
            }
            await Task.Delay(500);
            Save(Path.Combine(folder, $"{prefix}-{++index}.png"));
        }
        _busy = false;
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
