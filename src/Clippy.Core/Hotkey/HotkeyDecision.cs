namespace Clippy.Core.Hotkey;

/// <summary>
/// How Clippy captures the Win+V gesture on this machine.
/// </summary>
public enum HotkeyStrategy
{
    /// <summary>A native <c>RegisterHotKey</c> registration is active.</summary>
    Native,

    /// <summary>
    /// The combination is owned by something else (normally the Windows clipboard
    /// history panel), so a <c>WH_KEYBOARD_LL</c> hook suppresses it before the
    /// shell sees it.
    /// </summary>
    LowLevelHook,
}

/// <summary>The outcome of attempting a native hotkey registration.</summary>
public readonly record struct HotkeyRegistration(bool Succeeded, int Win32Error);

/// <summary>The chosen capture strategy plus what the user should be told.</summary>
public readonly record struct HotkeyPlan(
    HotkeyStrategy Strategy,
    bool PromptToDisableWindowsHistory,
    int Win32Error);

public static class HotkeyDecision
{
    /// <summary>Win32 <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>.</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>
    /// Picks a strategy from a native registration result.
    /// </summary>
    /// <remarks>
    /// Only 1409 justifies asking the user about Windows clipboard history, because
    /// only 1409 means someone else holds the combination. Any other failure gets a
    /// silent hook fallback: prompting about a feature that is not the cause would
    /// send the user chasing the wrong fix.
    /// </remarks>
    public static HotkeyPlan Decide(HotkeyRegistration registration)
    {
        if (registration.Succeeded)
        {
            return new HotkeyPlan(HotkeyStrategy.Native, PromptToDisableWindowsHistory: false, Win32Error: 0);
        }

        var isAlreadyRegistered = registration.Win32Error == ErrorHotkeyAlreadyRegistered;

        return new HotkeyPlan(
            HotkeyStrategy.LowLevelHook,
            PromptToDisableWindowsHistory: isAlreadyRegistered,
            registration.Win32Error);
    }
}
