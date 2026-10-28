using Clippy.Core.Hotkey;

namespace Clippy.Core.Tests;

public sealed class HotkeyBindingTests
{
    [Fact]
    public void Default_is_win_v()
    {
        var binding = HotkeyBinding.Default;

        Assert.Equal("Win+V", binding.Describe());
        Assert.True(binding.IsWindowsHistoryGesture);
        Assert.True(binding.UsesWindowsKey);
    }

    [Fact]
    public void Parse_accepts_common_aliases_and_round_trips()
    {
        Assert.True(HotkeyBinding.TryParse("control + shift + v", out var binding, out var error));
        Assert.Null(error);
        Assert.Equal("Ctrl+Shift+V", binding.Describe());
        Assert.Equal(HotkeyBinding.Control | HotkeyBinding.Shift, binding.Modifiers);
        Assert.Equal(0x56, binding.VirtualKey);
    }

    [Fact]
    public void Parse_accepts_function_keys_without_modifiers()
    {
        Assert.True(HotkeyBinding.TryParse("F9", out var binding, out _));

        Assert.Equal(0x78, binding.VirtualKey);
        Assert.Equal(0u, binding.Modifiers);
        Assert.Equal("F9", binding.Describe());
    }

    [Fact]
    public void Parse_accepts_named_keys()
    {
        Assert.True(HotkeyBinding.TryParse("Ctrl+Alt+PageDown", out var binding, out _));

        Assert.Equal(0x22, binding.VirtualKey);
        Assert.Equal("Ctrl+Alt+PageDown", binding.Describe());
    }

    [Fact]
    public void Parse_rejects_a_bare_letter()
    {
        Assert.False(HotkeyBinding.TryParse("V", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Modifier_key_cannot_be_the_main_key()
    {
        Assert.False(HotkeyBinding.TryCreate(HotkeyBinding.Control, 0x11, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Reserved_windows_chords_are_rejected()
    {
        Assert.False(HotkeyBinding.TryCreate(HotkeyBinding.Win, 0x4C, out _, out var error));
        Assert.NotNull(error);

        Assert.False(HotkeyBinding.TryCreate(HotkeyBinding.Control | HotkeyBinding.Alt, 0x2E, out _, out error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Unknown_key_name_is_reported()
    {
        Assert.False(HotkeyBinding.TryParse("Ctrl+Nonsense", out _, out var error));
        Assert.Contains("Nonsense", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Extra_modifier_bits_are_dropped()
    {
        Assert.True(HotkeyBinding.TryCreate(0xFFFF, 0x56, out var binding, out _));

        Assert.Equal(HotkeyBinding.AllModifiers, binding.Modifiers);
        Assert.Equal("Ctrl+Alt+Shift+Win+V", binding.Describe());
    }
}
