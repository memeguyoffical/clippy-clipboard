using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Clippy.App.Ui;

/// <summary>
/// Numeric field with a dark edit area. The inner edit is a native control whose
/// background comes from the parent's WM_CTLCOLOREDIT response, so the message is
/// answered here with a dark brush instead of the system window color.
/// </summary>
internal sealed class DarkNumericUpDown : NumericUpDown
{
    private const int WmCtlColorEdit = 0x0133;

    private IntPtr _brush;

    internal DarkNumericUpDown()
    {
        BackColor = Theme.SurfaceRaised;
        ForeColor = Theme.Text;
        BorderStyle = BorderStyle.FixedSingle;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RebuildBrush();
        RecolorChildren();
    }

    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        RebuildBrush();
        RecolorChildren();
        Invalidate();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmCtlColorEdit && _brush != IntPtr.Zero)
        {
            SetTextColor(message.WParam, ColorTranslator.ToWin32(ForeColor));
            SetBkColor(message.WParam, ColorTranslator.ToWin32(BackColor));
            message.Result = _brush;
            return;
        }

        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _brush != IntPtr.Zero)
        {
            DeleteObject(_brush);
            _brush = IntPtr.Zero;
        }

        base.Dispose(disposing);
    }

    private void RebuildBrush()
    {
        if (_brush != IntPtr.Zero)
        {
            DeleteObject(_brush);
        }

        _brush = CreateSolidBrush(ColorTranslator.ToWin32(BackColor));
    }

    private void RecolorChildren()
    {
        // The spinner and the edit box both inherit the palette so the digits
        // stay readable while the field is focused.
        foreach (Control child in Controls)
        {
            child.BackColor = BackColor;
            child.ForeColor = ForeColor;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr deviceContext, int color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkColor(IntPtr deviceContext, int color);
}
