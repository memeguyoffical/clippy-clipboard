using Clippy.Core.Hotkey;

namespace Clippy.Core.Tests;

public sealed class HotkeyDecisionTests
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    [Fact]
    public void Native_registration_success_uses_native_hotkey_and_does_not_prompt()
    {
        var plan = HotkeyDecision.Decide(new HotkeyRegistration(Succeeded: true, Win32Error: 0));

        Assert.Equal(HotkeyStrategy.Native, plan.Strategy);
        Assert.False(plan.PromptToDisableWindowsHistory);
    }

    [Fact]
    public void Already_registered_error_falls_back_to_hook_and_prompts_about_windows_history()
    {
        var plan = HotkeyDecision.Decide(
            new HotkeyRegistration(Succeeded: false, Win32Error: ErrorHotkeyAlreadyRegistered));

        Assert.Equal(HotkeyStrategy.LowLevelHook, plan.Strategy);
        Assert.True(plan.PromptToDisableWindowsHistory);
    }

    [Fact]
    public void Unexpected_registration_error_falls_back_to_hook_without_misleading_prompt()
    {
        var plan = HotkeyDecision.Decide(new HotkeyRegistration(Succeeded: false, Win32Error: 1400));

        Assert.Equal(HotkeyStrategy.LowLevelHook, plan.Strategy);
        Assert.False(plan.PromptToDisableWindowsHistory);
    }

    [Fact]
    public void Unexpected_registration_error_is_reported_for_diagnostics()
    {
        var plan = HotkeyDecision.Decide(new HotkeyRegistration(Succeeded: false, Win32Error: 1400));

        Assert.Equal(1400, plan.Win32Error);
    }
}
