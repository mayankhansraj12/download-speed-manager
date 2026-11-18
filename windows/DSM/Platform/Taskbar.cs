using System.Runtime.InteropServices;

namespace Dsm.Platform;

/// <summary>Taskbar button progress states; Windows draws Normal green, Paused yellow and Error red.</summary>
internal enum TaskbarState { None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }

/// <summary>Shows download progress on a window's taskbar button (ITaskbarList3).</summary>
internal static class Taskbar
{
    private static readonly ITaskbarList3? List = Create();

    private static ITaskbarList3? Create()
    {
        try
        {
            var list = (ITaskbarList3)new TaskbarListClass();
            list.HrInit();
            return list;
        }
        catch
        {
            return null; // no taskbar (e.g. Windows Server Core); progress is simply not shown
        }
    }

    /// <summary>Call on the UI thread with a window whose handle exists.</summary>
    public static void Set(IWin32Window window, TaskbarState state, double fraction = 0)
    {
        if (List is null) return;
        try
        {
            List.SetProgressState(window.Handle, state);
            if (state is TaskbarState.Normal or TaskbarState.Paused or TaskbarState.Error)
                List.SetProgressValue(window.Handle, (ulong)Math.Round(Math.Clamp(fraction, 0, 1) * 1000), 1000);
        }
        catch
        {
            // The button may not exist yet (window hidden); try again on the next update.
        }
    }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3 (only the progress methods are used)
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, TaskbarState state);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListClass;
}
