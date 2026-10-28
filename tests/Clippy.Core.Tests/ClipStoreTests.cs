using Clippy.Core.Models;
using Clippy.Core.Security;
using Clippy.Core.Storage;

namespace Clippy.Core.Tests;

public sealed class ClipStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly KeyRing _keys;
    private readonly ClipStore _store;

    public ClipStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "clippy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _keys = KeyRing.LoadOrCreate(_directory);
        _store = ClipStore.Open(Path.Combine(_directory, "test.db"), _keys);
    }

    [Fact]
    public void Stored_payload_round_trips()
    {
        var payload = new ClipPayload { Text = "round trip ☕", SourceApp = "notepad" };

        var added = _store.Add(payload);
        var loaded = _store.LoadPayload(added.Clip.Id);

        Assert.NotNull(loaded);
        Assert.Equal("round trip ☕", loaded!.Text);
        Assert.Equal("notepad", loaded.SourceApp);
    }

    [Fact]
    public void Database_file_does_not_contain_plaintext()
    {
        _store.Add(new ClipPayload { Text = "super-secret-marker-77" });

        var asText = ReadDatabaseText();

        Assert.DoesNotContain("super-secret-marker-77", asText, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_metadata_is_not_stored_in_plaintext()
    {
        _store.Add(new ClipPayload
        {
            Text = "body",
            SourceApp = "source-app-marker-4f21",
            SourceTitle = "window-title-marker-9b7c",
        });

        var asText = ReadDatabaseText();

        Assert.DoesNotContain("source-app-marker-4f21", asText, StringComparison.Ordinal);
        Assert.DoesNotContain("window-title-marker-9b7c", asText, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_copies_are_not_stored_twice()
    {
        var payload = new ClipPayload { Text = "same" };

        var first = _store.Add(payload);
        var second = _store.Add(new ClipPayload { Text = "same" });

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Single(_store.LoadAll());
        Assert.True(second.Clip.UseCount >= 1);
    }

    [Fact]
    public void Same_content_from_a_different_app_reuses_the_row()
    {
        var first = _store.Add(new ClipPayload
        {
            Text = "shared",
            SourceApp = "chrome",
            SourceTitle = "tab",
        });

        var second = _store.Add(new ClipPayload
        {
            Text = "shared",
            SourceApp = "notepad",
            SourceTitle = "untitled",
        });

        Assert.True(first.IsNew);
        Assert.False(second.IsNew);
        Assert.Equal(first.Clip.Id, second.Clip.Id);
        Assert.Equal("notepad", second.Clip.SourceApp);
        Assert.Equal("untitled", second.Clip.SourceTitle);
        Assert.Single(_store.LoadAll());
    }

    [Fact]
    public void Pinned_entries_do_not_consume_the_history_budget()
    {
        var pinned = _store.Add(new ClipPayload { Text = "keep me" }).Clip;
        _store.SetPinned(pinned.Id, true);

        for (var i = 0; i < 20; i++)
        {
            _store.Add(new ClipPayload { Text = $"filler {i}" });
        }

        _store.Prune(new RetentionPolicy(MaxEntries: 2, RetentionDays: 3650, MaxBytes: long.MaxValue), DateTimeOffset.UtcNow);

        var remaining = _store.LoadAll();

        Assert.Contains(remaining, clip => clip.Id == pinned.Id);
        Assert.Equal(2, remaining.Count(clip => !clip.Pinned));
        Assert.Equal(3, remaining.Count);
    }

    [Fact]
    public void Pinned_entries_survive_expiry()
    {
        var pinned = _store.Add(new ClipPayload { Text = "never expires" }).Clip;
        _store.SetPinned(pinned.Id, true);

        _store.Prune(new RetentionPolicy(500, 1, long.MaxValue), DateTimeOffset.UtcNow.AddYears(5));

        Assert.Single(_store.LoadAll());
    }

    [Fact]
    public void Unpinned_entries_expire()
    {
        _store.Add(new ClipPayload { Text = "temporary" });

        var removed = _store.Prune(new RetentionPolicy(500, 1, long.MaxValue), DateTimeOffset.UtcNow.AddDays(3));

        Assert.Equal(1, removed);
        Assert.Empty(_store.LoadAll());
    }

    [Fact]
    public void Pinned_entries_are_exempt_from_the_byte_cap()
    {
        var pinned = _store.Add(new ClipPayload { Text = new string('x', 5000) }).Clip;
        _store.SetPinned(pinned.Id, true);

        _store.Prune(new RetentionPolicy(500, 3650, MaxBytes: 1), DateTimeOffset.UtcNow);

        Assert.Contains(_store.LoadAll(), clip => clip.Id == pinned.Id);
    }

    [Fact]
    public void Pinned_bytes_do_not_delete_unpinned_clips()
    {
        var pinned = _store.Add(new ClipPayload { Text = new string('p', 5000) }).Clip;
        _store.SetPinned(pinned.Id, true);
        _store.Add(new ClipPayload { Text = "small unpinned" });

        // Unpinned bytes alone sit well under the cap. Counting pinned bytes
        // toward the total used to delete every unpinned clip here.
        _store.Prune(new RetentionPolicy(500, 3650, MaxBytes: 1000), DateTimeOffset.UtcNow);

        var remaining = _store.LoadAll();

        Assert.Equal(2, remaining.Count);
    }

    [Fact]
    public void Clear_without_pinned_keeps_pinned_entries()
    {
        var kept = _store.Add(new ClipPayload { Text = "kept" }).Clip;
        _store.SetPinned(kept.Id, true);
        _store.Add(new ClipPayload { Text = "dropped" });

        _store.Clear(includePinned: false);

        var remaining = _store.LoadAll();
        Assert.Single(remaining);
        Assert.Equal(kept.Id, remaining[0].Id);
    }

    [Fact]
    public void Search_metadata_reflects_pin_state()
    {
        var clip = _store.Add(new ClipPayload { Text = "searchable" }).Clip;
        _store.SetPinned(clip.Id, true);

        var reloaded = _store.Get(clip.Id);

        Assert.NotNull(reloaded);
        Assert.True(reloaded!.Pinned);
    }

    [Fact]
    public void Nickname_round_trips_through_load_and_get()
    {
        var clip = _store.Add(new ClipPayload { Text = "hunter2-the-password" }).Clip;

        _store.SetNickname(clip.Id, "  email password  ");

        Assert.Equal("email password", _store.Get(clip.Id)?.Nickname);
        Assert.Equal("email password", _store.LoadAll().Single().Nickname);
    }

    [Fact]
    public void Setting_an_empty_nickname_removes_it()
    {
        var clip = _store.Add(new ClipPayload { Text = "secret" }).Clip;
        _store.SetNickname(clip.Id, "bank");

        _store.SetNickname(clip.Id, "   ");

        Assert.Equal(string.Empty, _store.Get(clip.Id)?.Nickname);
    }

    [Fact]
    public void Nickname_is_not_stored_in_plaintext()
    {
        var clip = _store.Add(new ClipPayload { Text = "body" }).Clip;

        _store.SetNickname(clip.Id, "nickname-marker-5ad1");

        Assert.DoesNotContain("nickname-marker-5ad1", ReadDatabaseText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nickname_survives_dedupe_and_is_searchable()
    {
        var clip = _store.Add(new ClipPayload { Text = "hunter2" }).Clip;
        _store.SetPinned(clip.Id, true);
        _store.SetNickname(clip.Id, "router admin");

        var again = _store.Add(new ClipPayload { Text = "hunter2" });
        var candidate = _store.LoadAll().Single().ToCandidate();

        Assert.False(again.IsNew);
        Assert.Equal(clip.Id, again.Clip.Id);
        Assert.Equal("router admin", again.Clip.Nickname);
        Assert.Equal("router admin", candidate.Nickname);
    }

    private string ReadDatabaseText()
    {
        // SQLite holds the file open, so read it with a permissive share mode.
        var path = Path.Combine(_directory, "test.db");
        byte[] raw;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            raw = buffer.ToArray();
        }

        return System.Text.Encoding.UTF8.GetString(raw);
    }

    public void Dispose()
    {
        _store.Dispose();
        _keys.Dispose();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
