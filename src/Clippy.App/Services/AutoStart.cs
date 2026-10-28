using Microsoft.Win32;

namespace Clippy.App.Services;

/// <summary>Start-with-Windows via the per-user Run key. No admin, no scheduler.</summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Clippy";

    internal static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var executable = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(executable))
            {
                key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
            }
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is not null;
    }
}
