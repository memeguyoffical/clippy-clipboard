using System.Globalization;
using System.Text;

namespace Clippy.Core.Clipboard;

/// <summary>
/// Reads and writes the Windows <c>CF_HTML</c> clipboard format.
/// The header offsets are <b>UTF-8 byte</b> offsets, not character counts —
/// getting that wrong is the classic "rich paste is mangled" bug.
/// </summary>
public static class CfHtml
{
    public const string FormatName = "HTML Format";

    private const string HeaderFormat =
        "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";

    private const string Prefix = "<html><body><!--StartFragment-->";
    private const string Suffix = "<!--EndFragment--></body></html>";

    /// <summary>Wraps an HTML fragment in a valid CF_HTML document.</summary>
    public static string Build(string fragment)
    {
        fragment ??= string.Empty;

        // Every number is zero-padded to 10 digits, so the header length is
        // constant and can be measured before the real offsets are known.
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, HeaderFormat, 0, 0, 0, 0));

        var startFragment = headerLength + Encoding.UTF8.GetByteCount(Prefix);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(Suffix);

        var header = string.Format(
            CultureInfo.InvariantCulture,
            HeaderFormat,
            headerLength,
            endHtml,
            startFragment,
            endFragment);

        return string.Concat(header, Prefix, fragment, Suffix);
    }

    /// <summary>Pulls the fragment back out of a CF_HTML document.</summary>
    public static string? ExtractFragment(string? cfHtml)
    {
        if (string.IsNullOrEmpty(cfHtml))
        {
            return null;
        }

        var start = ReadOffset(cfHtml, "StartFragment:");
        var end = ReadOffset(cfHtml, "EndFragment:");

        if (start < 0 || end < 0 || end < start)
        {
            return cfHtml;
        }

        var bytes = Encoding.UTF8.GetBytes(cfHtml);

        if (end <= bytes.Length)
        {
            return Encoding.UTF8.GetString(bytes, start, end - start);
        }

        // Some producers write character offsets instead of byte offsets.
        return end <= cfHtml.Length ? cfHtml[start..end] : cfHtml;
    }

    private static int ReadOffset(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

        if (index < 0)
        {
            return -1;
        }

        var cursor = index + marker.Length;

        while (cursor < value.Length && value[cursor] == ' ')
        {
            cursor++;
        }

        var start = cursor;

        while (cursor < value.Length && char.IsAsciiDigit(value[cursor]))
        {
            cursor++;
        }

        if (cursor == start)
        {
            return -1;
        }

        return int.TryParse(value.AsSpan(start, cursor - start), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : -1;
    }
}
