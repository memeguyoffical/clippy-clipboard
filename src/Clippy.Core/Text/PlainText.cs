using System.Text;
using System.Text.RegularExpressions;

namespace Clippy.Core.Text;

/// <summary>Best-effort plain-text extraction from markup formats.</summary>
public static partial class PlainText
{
    [GeneratedRegex(@"<script\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptBlock();

    [GeneratedRegex(@"<style\b[^>]*>.*?</style>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlock();

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</li>|</tr>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\f\v]{2,}")]
    private static partial Regex SquashSpaces();

    private static readonly HashSet<string> IgnoredDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "fldinst",
        "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr", "footerf",
        "themedata", "colorschememapping", "datastore", "latentstyles", "listtable",
        "listoverridetable", "rsidtbl", "generator", "xmlnstbl",
    };

    public static string FromHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var working = ScriptBlock().Replace(html, " ");
        working = StyleBlock().Replace(working, " ");
        working = BlockBreak().Replace(working, "\n");
        working = Tag().Replace(working, " ");
        working = DecodeEntities(working);

        return Normalize(working);
    }

    public static string FromRtf(string rtf)
    {
        if (string.IsNullOrEmpty(rtf))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(rtf.Length);
        var depth = 0;
        var skipDepth = -1;
        var index = 0;

        while (index < rtf.Length)
        {
            var ch = rtf[index];

            switch (ch)
            {
                case '{':
                    depth++;

                    if (skipDepth < 0 && StartsIgnoredGroup(rtf, index + 1))
                    {
                        skipDepth = depth;
                    }

                    index++;
                    continue;

                case '}':
                    if (skipDepth == depth)
                    {
                        skipDepth = -1;
                    }

                    depth--;
                    index++;
                    continue;

                case '\\':
                    if (skipDepth >= 0)
                    {
                        // Still walk braces, but no control word output leaks.
                        index++;
                        continue;
                    }

                    index = ConsumeControlWord(rtf, index, builder);
                    continue;

                default:
                    if (skipDepth < 0)
                    {
                        builder.Append(ch);
                    }

                    index++;
                    continue;
            }
        }

        return Normalize(builder.ToString());
    }

    /// <summary>
    /// Font tables, stylesheets and <c>\*</c> destinations carry no visible
    /// text. Feeding them to the plain-text walker leaked font names and hidden
    /// markup, so the whole group is skipped instead.
    /// </summary>
    private static bool StartsIgnoredGroup(string rtf, int index)
    {
        while (index < rtf.Length && char.IsWhiteSpace(rtf[index]))
        {
            index++;
        }

        if (index >= rtf.Length || rtf[index] != '\\')
        {
            return false;
        }

        if (index + 1 < rtf.Length && rtf[index + 1] == '*')
        {
            return true;
        }

        return ReadControlWord(rtf, index + 1) is { } word && IgnoredDestinations.Contains(word);
    }

    private static string? ReadControlWord(string rtf, int index)
    {
        if (index >= rtf.Length || !char.IsLetter(rtf[index]))
        {
            return null;
        }

        var start = index;

        while (index < rtf.Length && char.IsLetter(rtf[index]))
        {
            index++;
        }

        return rtf[start..index];
    }

    private static int ConsumeControlWord(string rtf, int index, StringBuilder output)
    {
        var cursor = index + 1;

        if (cursor >= rtf.Length)
        {
            return cursor;
        }

        var marker = rtf[cursor];

        // Escaped literal characters: \\ \{ \}
        if (marker is '\\' or '{' or '}')
        {
            output.Append(marker);
            return cursor + 1;
        }

        // \'hh hex escapes
        if (marker == '\'')
        {
            if (cursor + 3 <= rtf.Length - 1
                && int.TryParse(rtf.AsSpan(cursor + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                output.Append((char)code);
                return cursor + 3;
            }

            return cursor + 1;
        }

        // \* marks ignorable destinations; those are handled by the group writer.
        if (!char.IsLetter(marker))
        {
            // Control symbols (\~ \- \_ ...) carry no indexable text.
            return cursor + 1;
        }

        var wordStart = cursor;
        while (cursor < rtf.Length && char.IsLetter(rtf[cursor]))
        {
            cursor++;
        }

        var word = rtf.AsSpan(wordStart, cursor - wordStart);

        // Skip numeric parameter.
        while (cursor < rtf.Length && (char.IsDigit(rtf[cursor]) || rtf[cursor] == '-'))
        {
            cursor++;
        }

        if (cursor < rtf.Length && rtf[cursor] == ' ')
        {
            cursor++;
        }

        if (word.SequenceEqual("par") || word.SequenceEqual("line"))
        {
            output.Append('\n');
        }
        else if (word.SequenceEqual("tab"))
        {
            output.Append('\t');
        }

        return cursor;
    }

    private static string DecodeEntities(string value)
    {
        return value
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&lt;", "<", StringComparison.OrdinalIgnoreCase)
            .Replace("&gt;", ">", StringComparison.OrdinalIgnoreCase)
            .Replace("&quot;", "\"", StringComparison.OrdinalIgnoreCase)
            .Replace("&#39;", "'", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        // Squash runs inside a line only. Treating newlines as generic
        // whitespace collapsed every paragraph into one long line.
        var lines = value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => SquashSpaces().Replace(line, " ").Trim())
            .Where(line => line.Length > 0);

        return string.Join(Environment.NewLine, lines).Trim();
    }
}
