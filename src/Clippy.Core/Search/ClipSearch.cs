namespace Clippy.Core.Search;

/// <summary>The projection of a stored clip that search needs.</summary>
public readonly record struct SearchCandidate(
    long Id,
    string Text,
    string Source,
    bool Pinned,
    long LastUsedUnixMs,
    int UseCount,
    string Nickname = "");

/// <summary>
/// Linear, in-memory ranking. History is capped (a few hundred entries), so a
/// scored scan is well under a millisecond and needs no index to keep in sync.
/// </summary>
public static class ClipSearch
{
    public static IReadOnlyList<long> Rank(IReadOnlyList<SearchCandidate> items, string? query)
    {
        if (items.Count == 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return items
                .OrderByDescending(item => item.Pinned)
                .ThenByDescending(item => item.LastUsedUnixMs)
                .Select(item => item.Id)
                .ToList();
        }

        var trimmed = query.Trim();

        return items
            .Select(item => (item, score: Score(item, trimmed)))
            .Where(pair => pair.score > 0)
            .OrderByDescending(pair => pair.score)
            .ThenByDescending(pair => pair.item.Pinned)
            .ThenByDescending(pair => pair.item.LastUsedUnixMs)
            .Select(pair => pair.item.Id)
            .ToList();
    }

    /// <summary>Zero means "no match". Higher is better.</summary>
    public static int Score(in SearchCandidate item, string query)
    {
        var textScore = MatchText(item.Text, query);
        var sourceScore = MatchText(item.Source, query) / 4;
        var nicknameScore = MatchText(item.Nickname, query);

        if (textScore == 0 && sourceScore == 0 && nicknameScore == 0)
        {
            return 0;
        }

        // A nickname is an explicit label, so a match on it should beat the same
        // match buried in a preview.
        var total = Math.Max(Math.Max(textScore, sourceScore), nicknameScore + 150);

        if (item.Pinned)
        {
            total += 40;
        }

        // Repeated use is a weak signal; it must never outrank a good text match.
        total += Math.Min(item.UseCount, 20);

        return total;
    }

    private static int MatchText(string? haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack))
        {
            return 0;
        }

        var text = haystack.AsSpan();
        var query = needle.AsSpan();

        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1000;
        }

        var wordStart = IndexOfWordStart(text, query);

        if (wordStart >= 0)
        {
            return 820 - Math.Min(wordStart, 60);
        }

        var anywhere = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);

        if (anywhere >= 0)
        {
            return 600 - Math.Min(anywhere, 80);
        }

        return SubsequenceScore(text, query);
    }

    private static int IndexOfWordStart(ReadOnlySpan<char> text, ReadOnlySpan<char> query)
    {
        var cursor = 0;

        while (cursor <= text.Length - query.Length)
        {
            var found = text[cursor..].IndexOf(query, StringComparison.OrdinalIgnoreCase);

            if (found < 0)
            {
                return -1;
            }

            var absolute = cursor + found;

            if (absolute == 0 || !char.IsLetterOrDigit(text[absolute - 1]))
            {
                return absolute;
            }

            cursor = absolute + 1;
        }

        return -1;
    }

    /// <summary>Fuzzy match: all query characters appear in order.</summary>
    private static int SubsequenceScore(ReadOnlySpan<char> text, ReadOnlySpan<char> query)
    {
        var queryIndex = 0;
        var gaps = 0;
        var lastMatch = -1;

        for (var i = 0; i < text.Length && queryIndex < query.Length; i++)
        {
            if (char.ToUpperInvariant(text[i]) != char.ToUpperInvariant(query[queryIndex]))
            {
                continue;
            }

            if (lastMatch >= 0)
            {
                gaps += i - lastMatch - 1;
            }

            lastMatch = i;
            queryIndex++;
        }

        if (queryIndex < query.Length)
        {
            return 0;
        }

        return Math.Max(120, 320 - gaps);
    }
}
