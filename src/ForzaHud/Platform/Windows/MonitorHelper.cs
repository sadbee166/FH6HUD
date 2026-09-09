using System.Runtime.InteropServices;

namespace ForzaHud.Platform.Windows;

/// <summary>
/// Monitor geometry, used to place the overlay on a specific display.
/// </summary>
public static class MonitorHelper
{
    /// <summary>Bounds of one display in virtual-screen coordinates.</summary>
    public readonly record struct MonitorBounds(int Left, int Top, int Width, int Height, bool IsPrimary);

    /// <summary>
    /// All displays, primary first. Falls back to the primary display when the requested
    /// index does not exist.
    /// </summary>
    public static IReadOnlyList<MonitorBounds> Enumerate()
    {
        var monitors = new List<MonitorBounds>();

        NativeMethods.MonitorEnumProc callback = (hMonitor, _, ref _, _) =>
        {
            var info = new NativeMethods.MONITORINFO
            {
                CbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };

            if (NativeMethods.GetMonitorInfoW(hMonitor, ref info))
            {
                monitors.Add(new MonitorBounds(
                    info.RcMonitor.Left,
                    info.RcMonitor.Top,
                    info.RcMonitor.Width,
                    info.RcMonitor.Height,
                    (info.DwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || monitors.Count == 0)
        {
            return [new MonitorBounds(0, 0, 1920, 1080, true)];
        }

        monitors.Sort((a, b) => (b.IsPrimary ? 1 : 0).CompareTo(a.IsPrimary ? 1 : 0));
        return monitors;
    }

    /// <summary>Bounds of the display at <paramref name="index"/>, clamped to a valid index.</summary>
    public static MonitorBounds Get(int index)
    {
        var monitors = Enumerate();
        return monitors[Math.Clamp(index, 0, monitors.Count - 1)];
    }
}
