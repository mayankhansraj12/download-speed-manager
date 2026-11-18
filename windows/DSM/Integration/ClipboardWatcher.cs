using Dsm.Engine;
using Dsm.Platform;
using Microsoft.Win32;

namespace Dsm.Integration;

/// <summary>Notices download links copied to the clipboard (WM_CLIPBOARDUPDATE on a message-only window).</summary>
internal sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly nint MessageOnlyParent = -3; // HWND_MESSAGE

    // Formats apps add to say "clipboard monitors, ignore this" (password managers, dictation tools).
    private static readonly string[] PrivateFormats = ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore"];

    public ClipboardWatcher()
    {
        CreateHandle(new CreateParams { Parent = MessageOnlyParent });
        Native.AddClipboardFormatListener(Handle);
        try
        {
            // Whatever is on the clipboard already is old news; never offer it when another app restores it.
            if (Clipboard.ContainsText()) Filter.MarkSeen(Clipboard.GetText());
        }
        catch
        {
            // Clipboard busy at startup.
        }
    }

    public ClipboardLinkFilter Filter { get; } = new();

    public bool Enabled { get; set; }

    public event Action<string>? LinkCopied;

    /// <summary>Don't react to text the app itself puts on the clipboard.</summary>
    public void Ignore(string text) => Filter.MarkSeen(text);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate) Check(); // also while disabled, so links seen then are never offered later
        base.WndProc(ref m);
    }

    private void Check()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var excluded = PrivateFormats.Any(Clipboard.ContainsData);
            if (Filter.Offer(Clipboard.GetText(), excluded, Enabled) is { } url) LinkCopied?.Invoke(url);
        }
        catch
        {
            // Another application is holding the clipboard.
        }
    }

    public void Dispose()
    {
        if (Handle == 0) return;
        Native.RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}

/// <summary>Start with Windows through the per-user Run key.</summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(AppPaths.AppId, $"\"{AppPaths.ExecutablePath}\" --tray");
        else key.DeleteValue(AppPaths.AppId, throwOnMissingValue: false);
    }
}
