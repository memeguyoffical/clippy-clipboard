using Clippy.Core.Models;
using Clippy.Core.Security;
using Clippy.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Clippy.Core.Tests;

public sealed class ClipStoreKeyTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "clippy-key-tests",
        Guid.NewGuid().ToString("N"));

    public ClipStoreKeyTests()
    {
        Directory.CreateDirectory(_directory);
    }

    private string DatabasePath => Path.Combine(_directory, "store.db");

    private KeyRing CreateKeys(string name) => KeyRing.LoadOrCreate(Path.Combine(_directory, "keys-" + name));

    [Fact]
    public void Reloading_the_key_file_gives_the_same_fingerprint()
    {
        using var first = CreateKeys("stable");
        using var second = CreateKeys("stable");

        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Database_from_another_key_is_rejected()
    {
        using (var keys = CreateKeys("owner"))
        using (var store = ClipStore.Open(DatabasePath, keys))
        {
            store.Add(new ClipPayload { Text = "owners secret" });
        }

        using var stranger = CreateKeys("stranger");

        var error = Assert.Throws<ClipStoreKeyException>(() => ClipStore.Open(DatabasePath, stranger));
        Assert.Equal(DatabasePath, error.DatabasePath);
    }

    [Fact]
    public void Legacy_database_without_fingerprint_opens_with_the_same_key()
    {
        using var keys = CreateKeys("legacy");

        using (var store = ClipStore.Open(DatabasePath, keys))
        {
            store.Add(new ClipPayload { Text = "from an older build" });
        }

        DeleteMetaRows();

        using var reopened = ClipStore.Open(DatabasePath, keys);
        Assert.Single(reopened.LoadAll());
    }

    [Fact]
    public void Legacy_database_without_fingerprint_is_rejected_for_another_key()
    {
        using (var keys = CreateKeys("legacy-owner"))
        using (var store = ClipStore.Open(DatabasePath, keys))
        {
            store.Add(new ClipPayload { Text = "not for you" });
        }

        DeleteMetaRows();

        using var stranger = CreateKeys("legacy-stranger");
        Assert.Throws<ClipStoreKeyException>(() => ClipStore.Open(DatabasePath, stranger));
    }

    [Fact]
    public void Unreadable_rows_are_skipped_instead_of_hiding_the_history()
    {
        using var keys = CreateKeys("rows");
        long brokenId;

        using (var store = ClipStore.Open(DatabasePath, keys))
        {
            store.Add(new ClipPayload { Text = "healthy row" });
            brokenId = store.Add(new ClipPayload { Text = "torn row" }).Clip.Id;
        }

        CorruptPreview(brokenId);

        using var reopened = ClipStore.Open(DatabasePath, keys);
        var skipped = new List<long>();
        var clips = reopened.LoadAll((id, _) => skipped.Add(id));

        Assert.Single(clips);
        Assert.Equal("healthy row", clips[0].Preview);
        Assert.Equal(new[] { brokenId }, skipped);
    }

    [Fact]
    public void Corrupt_duplicate_row_does_not_block_new_capture()
    {
        using var keys = CreateKeys("dedupe");
        long brokenId;

        using (var store = ClipStore.Open(DatabasePath, keys))
        {
            store.Add(new ClipPayload { Text = "repeat me" });
            brokenId = store.LoadAll().Single(clip => clip.Preview == "repeat me").Id;
        }

        CorruptPreview(brokenId);

        using (var reopened = ClipStore.Open(DatabasePath, keys))
        {
            var added = reopened.Add(new ClipPayload { Text = "repeat me" });

            Assert.True(added.IsNew);
            Assert.NotEqual(brokenId, added.Clip.Id);
            Assert.Single(reopened.LoadAll());
        }
    }

    [Fact]
    public void MoveAside_keeps_the_old_database_file()
    {
        using (var keys = CreateKeys("move"))
        using (var store = ClipStore.Open(DatabasePath, keys))
        {
            store.Add(new ClipPayload { Text = "keep this" });
        }

        var moved = ClipStore.MoveAside(DatabasePath);

        Assert.False(File.Exists(DatabasePath));
        Assert.Single(moved);
        Assert.True(File.Exists(moved[0]));
        Assert.Contains(".orphaned-", moved[0], StringComparison.Ordinal);
    }

    private void DeleteMetaRows()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM meta;";
        command.ExecuteNonQuery();
    }

    private void CorruptPreview(long id)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE clips SET preview_cipher = $cipher WHERE id = $id;";
        command.Parameters.AddWithValue("$cipher", new byte[32]);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
