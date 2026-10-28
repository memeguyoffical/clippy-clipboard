using System.Drawing;
using System.Windows.Forms;
using Clippy.App.Interop;
using Clippy.Core.Hotkey;

namespace Clippy.App.Ui;

/// <summary>
/// Read-only field that records a key chord instead of typing text. Reads the
/// Win key through <c>GetAsyncKeyState</c> because WinForms' modifier list does
/// not include it.
/// </summary>
internal sealed class HotkeyBox : TextBox
{
    private HotkeyBinding _binding;

    internal HotkeyBox(HotkeyBinding binding)
    {
        _binding = binding;
        ReadOnly = true;
        ShortcutsEnabled = false;
        TextAlign = HorizontalAlignment.Center;
        BackColor = Theme.SurfaceRaised;
        ForeColor = Theme.Text;
        BorderStyle = BorderStyle.FixedSingle;
        Cursor = Cursors.Hand;
        Font = new Font(Theme.FontFamily, 9f);
        Text = binding.Describe();
    }

    internal HotkeyBinding Binding => _binding;

    /// <summary>Raised with a human-readable reason when a chord is rejected.</summary>
    internal event Action<string>? Rejected;

    /// <summary>Raised when a new valid chord has been recorded.</summary>
    internal event Action? Accepted;

    protected override bool IsInputKey(Keys keyData) => keyData != Keys.Tab;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;

        var key = e.KeyCode;

        if (key is Keys.Back or Keys.Delete && Control.ModifierKeys == Keys.None)
        {
            SetBinding(HotkeyBinding.Default);
            return;
        }

        if (HotkeyBinding.IsModifierKey((int)key))
        {
            return;
        }

        if (!HotkeyBinding.TryCreate(CurrentModifiers(), (int)key, out var binding, out var error))
        {
            Rejected?.Invoke(error ?? "That combination cannot be used.");
            return;
        }

        SetBinding(binding);
    }

    private void SetBinding(HotkeyBinding binding)
    {
        _binding = binding;
        Text = binding.Describe();
        Accepted?.Invoke();
    }

    private static uint CurrentModifiers()
    {
        var modifiers = 0u;

        if (IsDown(NativeMethods.VK_CONTROL) || IsDown(NativeMethods.VK_LCONTROL) || IsDown(NativeMethods.VK_RCONTROL))
        {
            modifiers |= HotkeyBinding.Control;
        }

        if (IsDown(NativeMethods.VK_MENU) || IsDown(NativeMethods.VK_LMENU) || IsDown(NativeMethods.VK_RMENU))
        {
            modifiers |= HotkeyBinding.Alt;
        }

        if (IsDown(NativeMethods.VK_SHIFT) || IsDown(NativeMethods.VK_LSHIFT) || IsDown(NativeMethods.VK_RSHIFT))
        {
            modifiers |= HotkeyBinding.Shift;
        }

        if (IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN))
        {
            modifiers |= HotkeyBinding.Win;
        }

        return modifiers;
    }

    private static bool IsDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
}
