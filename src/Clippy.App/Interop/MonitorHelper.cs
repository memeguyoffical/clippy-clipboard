using System.Drawing;

namespace Clippy.App.Interop;

/// <summary>Screen geometry helpers, DPI-aware via the caller's coordinates.</summary>
internal static class MonitorHelper
{
    /// <summary>Work area (monitor bounds minus taskbar) of the monitor nearest a point.</summary>
    internal static Rectangle GetWorkArea(Point point)
    {
        var nativePoint = new NativeMethods.POINT { X = point.X, Y = point.Y };
        var monitor = NativeMethods.MonitorFromPoint(nativePoint, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return SystemInformation.WorkingArea;
        }

        return Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
    }

    /// <summary>
    /// Places a popup near the cursor, preferring below-right, flipping above when
    /// there is no room, and clamping so it never crosses a monitor edge.
    /// </summary>
    internal static Point PlaceNearCursor(Point cursor, Size size, int offsetX = 12, int offsetY = 16)
    {
        var work = GetWorkArea(cursor);

        var x = cursor.X + offsetX;
        var y = cursor.Y + offsetY;

        if (x + size.Width > work.Right)
        {
            x = cursor.X - size.Width - offsetX;
        }

        if (y + size.Height > work.Bottom)
        {
            y = cursor.Y - size.Height - offsetY;
        }

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height));

        return new Point(x, y);
    }
}
