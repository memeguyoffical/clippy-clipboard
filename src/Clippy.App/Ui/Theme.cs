using System.Drawing;

namespace Clippy.App.Ui;

/// <summary>Monochrome palette: near-black surfaces, white text.</summary>
internal static class Theme
{
    internal static readonly Color Background = Color.FromArgb(0x0A, 0x0A, 0x0B);
    internal static readonly Color Surface = Color.FromArgb(0x12, 0x12, 0x14);
    internal static readonly Color SurfaceRaised = Color.FromArgb(0x18, 0x18, 0x1B);
    internal static readonly Color Selection = Color.FromArgb(0x26, 0x26, 0x2B);
    internal static readonly Color Border = Color.FromArgb(0x2A, 0x2A, 0x2F);
    internal static readonly Color Text = Color.FromArgb(0xFF, 0xFF, 0xFF);
    internal static readonly Color TextMuted = Color.FromArgb(0x8C, 0x8C, 0x95);
    internal static readonly Color TextFaint = Color.FromArgb(0x5E, 0x5E, 0x66);

    internal const int BaseWidth = 460;
    internal const int BaseHeight = 540;
    internal const int BasePadding = 14;
    internal const int BaseHeaderHeight = 30;
    internal const int BaseSearchHeight = 36;
    internal const int BaseRowHeight = 56;
    internal const int BaseFooterHeight = 30;

    internal const string FontFamily = "Segoe UI";
}
