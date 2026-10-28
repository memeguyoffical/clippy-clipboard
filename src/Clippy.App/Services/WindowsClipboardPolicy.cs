using Clippy.App.Interop;
using Microsoft.Win32;

namespace Clippy.App.Services;

/// <summary>
/// Reads and writes the Windows clipboard-history policy. Touched only after the
/// user explicitly agrees to hand Win+V over.
/// </summary>
internal static class WindowsClipboardPolicy
{
    internal static bool IsHistoryEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(NativeMethods.ClipboardPolicyKeyPath);
        return key?.GetValue(NativeMethods.EnableClipboardHistoryValue) is not int value || value != 0;
    }

    /// <summary>
    /// Turns off Windows clipboard history and cloud clipboard sync, which frees
    /// the Win+V chord for a native registration.
    /// </summary>
    internal static void DisableHistory()
    {
        using var key = Registry.CurrentUser.CreateSubKey(NativeMethods.ClipboardPolicyKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the Windows clipboard policy key.");

        key.SetValue(NativeMethods.EnableClipboardHistoryValue, 0, RegistryValueKind.DWord);
        key.SetValue(NativeMethods.EnableCloudClipboardValue, 0, RegistryValueKind.DWord);
    }
}
