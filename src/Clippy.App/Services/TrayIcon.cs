using System.Drawing;
using Clippy.App.Ui;

namespace Clippy.App.Services;

/// <summary>Tray presence. Menu state mirrors the live settings.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _pauseItem;
    private bool _disposed;

    internal TrayIcon(Action open, Func<bool> isCaptureEnabled, Action toggleCapture, Action settings, Action clear, Action exit)
    {
        _icon = AppIconFactory.CreateTrayIcon();

        _menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.Text,
            ShowImageMargin = false,
            Font = new Font(Theme.FontFamily, 9f),
        };

        var openItem = new ToolStripMenuItem("Open Clippy", null, (_, _) => open())
        {
            Font = new Font(_menu.Font, FontStyle.Bold),
        };

        _pauseItem = new ToolStripMenuItem("Pause capture", null, (_, _) => toggleCapture());

        var settingsItem = new ToolStripMenuItem("Settings", null, (_, _) => settings());
        var clearItem = new ToolStripMenuItem("Clear history", null, (_, _) => clear());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => exit());

        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(clearItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _menu.Opening += (_, _) =>
            _pauseItem.Text = isCaptureEnabled() ? "Pause capture" : "Resume capture";

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Clippy - Win+V",
            Visible = true,
            ContextMenuStrip = _menu,
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                open();
            }
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    /// <summary>Professional renderer with the dark palette forced through.</summary>
    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.SurfaceRaised;

        public override Color ImageMarginGradientBegin => Theme.SurfaceRaised;

        public override Color ImageMarginGradientMiddle => Theme.SurfaceRaised;

        public override Color ImageMarginGradientEnd => Theme.SurfaceRaised;

        public override Color MenuBorder => Theme.Border;

        public override Color MenuItemBorder => Theme.Selection;

        public override Color MenuItemSelected => Theme.Selection;

        public override Color MenuItemSelectedGradientBegin => Theme.Selection;

        public override Color MenuItemSelectedGradientEnd => Theme.Selection;

        public override Color SeparatorDark => Theme.Border;

        public override Color SeparatorLight => Theme.SurfaceRaised;
    }
}
