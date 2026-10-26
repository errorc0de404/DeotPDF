using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PdfNotes.App.Views;

/// <summary>
/// Size of the work area (screen minus taskbar) of the monitor a window is on, in DIPs of that monitor (D13e). The app is
/// per-monitor DPI aware, so <see cref="SystemParameters.WorkArea"/> (primary monitor, system DPI) is not enough for a
/// dialog opened on a second screen with another scale.
/// </summary>
public static class ScreenArea
{
    /// <summary>Harness: a simulated work area size (e.g. a 1366×768 screen at 125 %).</summary>
    public static Size? Override { get; set; }

    /// <summary>Space a dialog keeps free around itself inside the work area.</summary>
    public const double DialogMargin = 40;

    /// <summary>Work area size of the monitor <paramref name="near"/> is on (the main window's when null or not shown yet).</summary>
    public static Size WorkArea(Window? near = null)
    {
        if (Override is { } size) return size;
        near ??= Application.Current?.MainWindow;
        nint handle = near is null ? 0 : new WindowInteropHelper(near).Handle;
        nint monitor = handle != 0 ? MonitorFromWindow(handle, MonitorDefaultToNearest) : MonitorFromPoint(default, MonitorDefaultToPrimary);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info) || GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY) != 0 || dpiX == 0 || dpiY == 0)
            return new(SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
        return new((info.Work.Right - info.Work.Left) * 96.0 / dpiX, (info.Work.Bottom - info.Work.Top) * 96.0 / dpiY);
    }

    /// <summary>
    /// Gives a resizable dialog its preferred size, shrunk to the work area (minus <see cref="DialogMargin"/>); the minimum size
    /// never exceeds what fits, so the dialog buttons stay on the screen at 150–200 % on small screens.
    /// </summary>
    public static void Fit(Window dialog, double width, double height, double minWidth, double minHeight)
    {
        var area = WorkArea();
        dialog.Width = Math.Max(1, Math.Min(width, area.Width - DialogMargin));
        dialog.Height = Math.Max(1, Math.Min(height, area.Height - DialogMargin));
        dialog.MinWidth = Math.Min(minWidth, dialog.Width);
        dialog.MinHeight = Math.Min(minHeight, dialog.Height);
    }

    private const uint MonitorDefaultToPrimary = 1, MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Bounds Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(ScreenPoint point, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
