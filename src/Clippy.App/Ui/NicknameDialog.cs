using System.Drawing;
using System.Windows.Forms;
using Clippy.App.Interop;

namespace Clippy.App.Ui;

/// <summary>Small dark dialog for naming a clip. Empty input removes the name.</summary>
internal sealed class NicknameDialog : Form
{
    private readonly Label _heading;
    private readonly TextBox _input;
    private readonly Label _hint;

    internal NicknameDialog(string? current)
    {
        Text = "Clip nickname";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(430, 190);
        Font = new Font(Theme.FontFamily, 9f);

        var heading = new Label
        {
            Text = "Nickname",
            AutoSize = false,
            Font = new Font(Theme.FontFamily, 11f, FontStyle.Bold),
            ForeColor = Theme.Text,
        };

        _heading = heading;

        _hint = new Label
        {
            Text = "Shown instead of the clip text, and searchable. Leave empty to remove.",
            AutoSize = false,
            ForeColor = Theme.TextMuted,
        };

        _input = new TextBox
        {
            Text = current ?? string.Empty,
            MaxLength = 80,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font(Theme.FontFamily, 10f),
        };

        var save = Button("Save");
        save.DialogResult = DialogResult.OK;

        var cancel = Button("Cancel");
        cancel.DialogResult = DialogResult.Cancel;

        Controls.AddRange([heading, _hint, _input, cancel, save]);

        heading.SetBounds(18, 14, 394, 26);
        _hint.SetBounds(18, 44, 394, 20);
        _input.SetBounds(18, 72, 394, 28);
        cancel.SetBounds(ClientSize.Width - 18 - 90, 130, 90, 32);
        save.SetBounds(ClientSize.Width - 18 - 188, 130, 90, 32);

        AcceptButton = save;
        CancelButton = cancel;
    }

    internal string Nickname => _input.Text.Trim();

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        var enabled = 1;

        if (NativeMethods.DwmSetWindowAttribute(
                Handle,
                NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref enabled,
                sizeof(int)) != 0)
        {
            NativeMethods.DwmSetWindowAttribute(
                Handle,
                NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY,
                ref enabled,
                sizeof(int));
        }
    }

    private static Button Button(string text)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.Text,
            DialogResult = DialogResult.None,
        };

        button.FlatAppearance.BorderColor = Theme.Border;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Font?.Dispose();
            _heading.Font?.Dispose();
            _input.Font?.Dispose();
        }

        base.Dispose(disposing);
    }
}
