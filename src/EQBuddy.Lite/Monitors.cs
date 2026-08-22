using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EQBuddy.Lite;

/// <summary>
/// The working area of the screen a window is actually on. WPF only exposes the PRIMARY
/// monitor's work area (<see cref="SystemParameters.WorkArea"/>) and the bounding box of
/// them all (<c>VirtualScreen*</c>) — neither answers "is there room beside this window",
/// which is what deciding where a spawned FEED goes comes down to. The panel's own default
/// position is hard against the right edge of a screen, so on a multi-monitor desk the
/// virtual-screen answer is "yes, plenty" and the new window opens on the next monitor
/// over, nowhere near the one it came from.
/// </summary>
internal static class Monitors
{
    /// <summary>The work area (screen minus taskbar) of the monitor holding most of
    /// <paramref name="w"/>, in WPF device-independent pixels. Falls back to the primary
    /// monitor's work area if the window has no handle yet or the call fails.</summary>
    public static Rect WorkAreaOf(Window w)
    {
        try
        {
            var handle = new WindowInteropHelper(w).Handle;
            if (handle == IntPtr.Zero) return SystemParameters.WorkArea;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;

            // Win32 speaks physical pixels; everything the layout code compares against
            // is DIPs, so divide by this window's own DPI scale.
            var scale = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice;
            var sx = scale is { M11: > 0 } ? scale.Value.M11 : 1;
            var sy = scale is { M22: > 0 } ? scale.Value.M22 : 1;
            return new Rect(
                info.rcWork.left / sx, info.rcWork.top / sy,
                (info.rcWork.right - info.rcWork.left) / sx,
                (info.rcWork.bottom - info.rcWork.top) / sy);
        }
        catch (Exception ex)
        {
            EQBuddy.Core.CoreLog.Error(ex);
            return SystemParameters.WorkArea;
        }
    }

    private const int MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public int dwFlags;
    }
}
