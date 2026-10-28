using System.Drawing;
using Clippy.App.Interop;

namespace Clippy.App.Ui;

/// <summary>A dark, rounded search input. WinForms has no themed one.</summary>
internal sealed class SearchField : Panel
{
    private string _cue = string.Empty;

    internal SearchField()
    {
        DoubleBuffered = true;
        BackColor = Theme.Background;
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.Text,
            ShortcutsEnabled = true,
        };

        Controls.Add(Box);
    }

    internal TextBox Box { get; }

    internal void SetCue(string cue)
    {
        _cue = cue;

        if (Box.IsHandleCreated)
        {
            NativeMethods.SendMessage(Box.Handle, NativeMethods.EM_SETCUEBANNER, IntPtr.Zero, cue);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (!string.IsNullOrEmpty(_cue))
        {
            // wParam 0 hides the cue as soon as the field has focus, so it never
            // sits underneath the caret.
            NativeMethods.SendMessage(Box.Handle, NativeMethods.EM_SETCUEBANNER, IntPtr.Zero, _cue);
        }
    }

    protected override void OnResize(EventArgs eventArgs)
    {
        base.OnResize(eventArgs);
        LayoutBox();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Math.Min(8, Height / 2);

        using var path = Rounded(bounds, radius);
        using var background = new SolidBrush(Theme.SurfaceRaised);
        e.Graphics.FillPath(background, path);

        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawPath(pen, path);
    }

    private void LayoutBox()
    {
        var horizontal = 12;
        var height = Box.PreferredHeight;
        Box.SetBounds(
            horizontal,
            Math.Max(0, (Height - height) / 2),
            Math.Max(10, Width - (horizontal * 2)),
            height);
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
