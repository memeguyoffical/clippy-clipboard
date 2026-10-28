using Clippy.Core.Text;

namespace Clippy.Core.Tests;

public sealed class PlainTextTests
{
    [Fact]
    public void Html_paragraphs_stay_on_separate_lines()
    {
        var text = PlainText.FromHtml("<p>first paragraph</p><p>second paragraph</p>");

        Assert.Equal($"first paragraph{Environment.NewLine}second paragraph", text);
    }

    [Fact]
    public void Html_line_breaks_are_preserved()
    {
        var text = PlainText.FromHtml("<p>first<br>second</p>");

        Assert.Equal($"first{Environment.NewLine}second", text);
    }

    [Fact]
    public void Html_inline_runs_collapse_to_single_spaces()
    {
        var text = PlainText.FromHtml("<p>one   two <b>three</b></span> four</p>");

        Assert.Equal("one two three four", text);
    }

    [Fact]
    public void Rtf_font_table_and_star_destinations_are_ignored()
    {
        const string rtf = @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}{\*\generator Msftedit 5;}{\*\htmltag <p>}visible text\par second line}";

        var text = PlainText.FromRtf(rtf);

        Assert.Equal($"visible text{Environment.NewLine}second line", text);
    }

    [Fact]
    public void Rtf_unknown_star_destination_is_ignored()
    {
        const string rtf = @"{\rtf1{\*\customdest hidden stuff}shown}";

        Assert.Equal("shown", PlainText.FromRtf(rtf));
    }

    [Fact]
    public void Rtf_nested_ignored_destination_does_not_leak()
    {
        const string rtf = @"{\rtf1{\stylesheet{\s0 Normal;}}{\*\latentstyles hidden}{\info{\title secret title}}kept}";

        Assert.Equal("kept", PlainText.FromRtf(rtf));
    }
}
