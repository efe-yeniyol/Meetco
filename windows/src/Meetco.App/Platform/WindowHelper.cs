using System.Windows;
using System.Windows.Interop;
using Meetco.Core.Infrastructure;

namespace MeetcoApp.Platform;

/// <summary>Native window tweaks for Meetco's floating windows.</summary>
public static class WindowHelper
{
    /// <summary>
    /// Makes a window a non-activating tool window: clicking it never takes focus away from the
    /// meeting app, and it stays out of Alt+Tab. Call from SourceInitialized.
    /// </summary>
    public static void MakeFloating(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var style = NativeMethods.GetWindowLong(handle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(handle, NativeMethods.GWL_EXSTYLE, style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Excludes the window from screen capture and screen sharing (Windows 10 2004+), or shows it
    /// again. Best effort: some sharing tools capture differently.
    /// </summary>
    public static void SetHiddenFromCapture(Window window, bool hidden)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        if (!hidden)
        {
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WDA_NONE);
            return;
        }
        if (NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE)) return;
        // Older Windows: the window shows up black in captures instead of disappearing.
        if (!NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WDA_MONITOR))
        {
            Log.Notice("app", "Could not hide the window from screen capture on this Windows version");
        }
    }
}
