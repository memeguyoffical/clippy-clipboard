using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Clippy.App.Interop;
using Microsoft.Win32;

namespace Clippy.App.Ui;

/// <summary>
/// Draws the Clippy mark: an outlined clipboard with a rounded clip tab.
/// Rendered at runtime so no binary asset ships and the stroke can flip
/// between black and white to stay visible on either taskbar.
/// </summary>
internal static class AppIconFactory
{
    internal static Icon CreateTrayIcon()
    {
        var stroke = TaskbarUsesLightTheme() ? Color.FromArgb(0x18, 0x18, 0x18) : Color.White;
        return Create(stroke, 32);
    }

    internal static Icon Create(Color stroke, int size = 32)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var unit = size / 32f;
            var thickness = Math.Max(1.6f, 2.6f * unit);

            using var pen = new Pen(stroke, thickness)
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            // Body
            using (var body = RoundedRect(new RectangleF(7 * unit, 7 * unit, 18 * unit, 21 * unit), 3.2f * unit))
            {
                graphics.DrawPath(pen, body);
            }

            // Clip tab straddling the top edge
            using (var tab = RoundedRect(new RectangleF(11.5f * unit, 3f * unit, 9 * unit, 6.4f * unit), 2.6f * unit))
            {
                graphics.DrawPath(pen, tab);
            }
        }

        var handle = bitmap.GetHicon();

        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static bool TaskbarUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}
