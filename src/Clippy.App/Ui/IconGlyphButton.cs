using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Clippy.App.Ui;

/// <summary>Small flat button that paints a Segoe MDL2 Assets glyph.</summary>
internal sealed class IconGlyphButton : Control
{
    private readonly string _glyph;
    private bool _hover;

    internal IconGlyphButton(string glyph, string accessibleName)
    {
        _glyph = glyph;
        AccessibleName = accessibleName;
        TabStop = false;
        Cursor = Cursors.Hand;
        ForeColor = Theme.TextMuted;
        SetStyle(
            ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Font? GlyphFont { get; set; }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        if (_hover)
        {
            using var path = Rounded(ClientRectangle, 4);
            using var brush = new SolidBrush(Theme.Selection);
            graphics.FillPath(brush, path);
        }

        if (GlyphFont is null)
        {
            return;
        }

        TextRenderer.DrawText(
            graphics,
            _glyph,
            GlyphFont,
            ClientRectangle,
            _hover ? Theme.Text : Theme.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var rect = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        var path = new GraphicsPath();

        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}
