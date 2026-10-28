using Clippy.Core.Search;

namespace Clippy.Core.Tests;

public sealed class ClipSearchTests
{
    private static SearchCandidate Candidate(long id, string text, bool pinned = false, long lastUsed = 0, int useCount = 0)
        => new(id, text, "chrome", pinned, lastUsed, useCount);

    [Fact]
    public void Prefix_match_outranks_a_mid_word_match()
    {
        var items = new List<SearchCandidate>
        {
            Candidate(1, "the apiary notes"),
            Candidate(2, "api reference guide"),
        };

        var ranked = ClipSearch.Rank(items, "api");

        Assert.Equal(2, ranked[0]);
    }

    [Fact]
    public void Pinned_wins_a_tie()
    {
        var items = new List<SearchCandidate>
        {
            Candidate(1, "invoice", pinned: false, lastUsed: 200),
            Candidate(2, "invoice", pinned: true, lastUsed: 100),
        };

        var ranked = ClipSearch.Rank(items, "invoice");

        Assert.Equal(2, ranked[0]);
    }

    [Fact]
    public void Empty_query_sorts_pinned_first_then_most_recent()
    {
        var items = new List<SearchCandidate>
        {
            Candidate(1, "old", lastUsed: 10),
            Candidate(2, "pinned", pinned: true, lastUsed: 1),
            Candidate(3, "new", lastUsed: 500),
        };

        var ranked = ClipSearch.Rank(items, "   ");

        Assert.Equal([2, 3, 1], ranked);
    }

    [Fact]
    public void Non_matching_query_returns_nothing()
    {
        var items = new List<SearchCandidate> { Candidate(1, "alpha"), Candidate(2, "beta") };

        Assert.Empty(ClipSearch.Rank(items, "zzzz"));
    }

    [Fact]
    public void Fuzzy_subsequence_still_matches()
    {
        var items = new List<SearchCandidate> { Candidate(1, "clipboard manager") };

        var ranked = ClipSearch.Rank(items, "cbm");

        Assert.Single(ranked);
    }

    [Fact]
    public void Source_application_is_searchable()
    {
        var items = new List<SearchCandidate> { Candidate(1, "anything") };

        Assert.Single(ClipSearch.Rank(items, "chrome"));
    }

    [Fact]
    public void Search_is_case_insensitive()
    {
        var items = new List<SearchCandidate> { Candidate(1, "Hello World") };

        Assert.Single(ClipSearch.Rank(items, "hello"));
    }

    [Fact]
    public void Use_count_cannot_outrank_a_better_text_match()
    {
        var items = new List<SearchCandidate>
        {
            Candidate(1, "the apiary notes", useCount: 100),
            Candidate(2, "api reference guide", useCount: 0),
        };

        var ranked = ClipSearch.Rank(items, "api");

        Assert.Equal(2, ranked[0]);
    }

    [Fact]
    public void Nickname_is_searchable()
    {
        var items = new List<SearchCandidate>
        {
            new(1, "hunter2", "chrome", true, 0, 0, "router admin"),
            Candidate(2, "router admin password", lastUsed: 500),
        };

        var ranked = ClipSearch.Rank(items, "router");

        Assert.Equal(1, ranked[0]);
    }

    [Fact]
    public void Nickname_match_beats_the_same_words_in_a_preview()
    {
        var items = new List<SearchCandidate>
        {
            Candidate(1, "my bank login notes", lastUsed: 900),
            new(2, "hunter2", "chrome", true, 0, 0, "bank"),
        };

        var ranked = ClipSearch.Rank(items, "bank");

        Assert.Equal(2, ranked[0]);
    }
}
