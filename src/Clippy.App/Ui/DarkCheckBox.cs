using System.Drawing;
using System.Windows.Forms;

namespace Clippy.App.Ui;

/// <summary>
/// Checkbox drawn by hand. The themed glyph is invisible on a black surface and
/// the flat style ignores ForeColor, so both states are painted explicitly.
/// </summary>
internal sealed class DarkCheckBox : CheckBox
{
    internal DarkCheckBox()
    {
        AutoSize = false;
        BackColor = Color.Transparent;
        ForeColor = Theme.Text;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        graphics.Clear(Parent?.BackColor ?? Theme.Background);

        var size = Math.Min(16, Height);
        var box = new Rectangle(0, (Height - size) / 2, size, size);

        using (var path = Rounded(box, 4))
        using (var fill = new SolidBrush(Checked ? Theme.Text : Theme.SurfaceRaised))
        using (var border = new Pen(Checked ? Theme.Text : Theme.Border))
        {
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);
        }

        if (Checked)
        {
            using var tick = new Pen(Theme.Background, Math.Max(1.6f, size / 9f))
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };

            var left = box.Left + (size * 0.24f);
            var mid = box.Left + (size * 0.44f);
            var right = box.Left + (size * 0.76f);
            var top = box.Top + (size * 0.30f);
            var bottom = box.Top + (size * 0.68f);

            graphics.DrawLines(tick, [new PointF(left, bottom - (size * 0.16f)), new PointF(mid, bottom), new PointF(right, top)]);
        }

        var textBounds = new Rectangle(box.Right + 10, 0, Math.Max(0, Width - box.Right - 10), Height);

        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            textBounds,
            Enabled ? Theme.Text : Theme.TextFaint,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}
