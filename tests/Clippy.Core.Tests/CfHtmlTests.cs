using System.Text;
using Clippy.Core.Clipboard;

namespace Clippy.Core.Tests;

public sealed class CfHtmlTests
{
    [Fact]
    public void Extract_returns_the_original_fragment()
    {
        const string fragment = "<b>hello</b> world";
        var document = CfHtml.Build(fragment);

        Assert.Equal(fragment, CfHtml.ExtractFragment(document));
    }

    [Fact]
    public void Header_offsets_point_at_the_fragment_as_utf8_bytes()
    {
        // Non-ASCII forces byte length and char count apart.
        const string fragment = "<p>café ☕ naïve</p>";
        var document = CfHtml.Build(fragment);

        var start = ReadHeader(document, "StartFragment:");
        var end = ReadHeader(document, "EndFragment:");

        var bytes = Encoding.UTF8.GetBytes(document);
        var extracted = Encoding.UTF8.GetString(bytes, start, end - start);

        Assert.Equal(fragment, extracted);
    }

    [Fact]
    public void Document_starts_with_the_required_version_line()
    {
        var document = CfHtml.Build("<i>x</i>");

        Assert.StartsWith("Version:0.9\r\n", document, StringComparison.Ordinal);
    }

    [Fact]
    public void End_html_offset_covers_the_whole_document()
    {
        var document = CfHtml.Build("<i>x</i>");

        Assert.Equal(Encoding.UTF8.GetByteCount(document), ReadHeader(document, "EndHTML:"));
    }

    [Fact]
    public void Missing_offsets_fall_back_to_the_raw_value()
    {
        Assert.Equal("<p>raw</p>", CfHtml.ExtractFragment("<p>raw</p>"));
    }

    [Fact]
    public void Empty_input_is_rejected_rather_than_throwing()
    {
        Assert.Null(CfHtml.ExtractFragment(null));
        Assert.Null(CfHtml.ExtractFragment(string.Empty));
    }

    private static int ReadHeader(string document, string marker)
    {
        var index = document.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = index;

        while (end < document.Length && char.IsAsciiDigit(document[end]))
        {
            end++;
        }

        return int.Parse(document.AsSpan(index, end - index));
    }
}
