using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dsm.Platform;

internal static partial class Native
{
    private const uint FsctlSetSparse = 0x000900C4;
    private static readonly Guid DownloadsFolder = new("374DE290-123F-4565-9164-39C4925E467B");

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint inBuffer, uint inSize,
        nint outBuffer, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AddClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RemoveClipboardFormatListener(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);

    /// <summary>
    /// Mark a file sparse so out-of-order segment writes don't make NTFS zero-fill
    /// everything before them. Needs a synchronous handle; failures (FAT, exFAT) are harmless.
    /// </summary>
    public static void TryMarkSparse(SafeFileHandle handle)
    {
        try { DeviceIoControl(handle, FsctlSetSparse, 0, 0, 0, 0, out _, 0); } catch { /* not supported */ }
    }

    /// <summary>The user's real Downloads folder (it can be redirected to another drive).</summary>
    public static string DownloadsPath()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsFolder, 0, 0, out var ptr) == 0)
            {
                try { return Marshal.PtrToStringUni(ptr)!; }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
        }
        catch { /* fall through */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
