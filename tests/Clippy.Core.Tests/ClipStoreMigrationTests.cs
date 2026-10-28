using System.Text.Json;
using Clippy.Core.Models;
using Clippy.Core.Security;
using Clippy.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Clippy.Core.Tests;

public sealed class ClipStoreMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "clippy-migration-tests",
        Guid.NewGuid().ToString("N"));

    private readonly KeyRing _keys;

    public ClipStoreMigrationTests()
    {
        Directory.CreateDirectory(_directory);
        _keys = KeyRing.LoadOrCreate(_directory);
    }

    [Fact]
    public void Legacy_plaintext_source_columns_are_sealed_and_cleared()
    {
        var path = Path.Combine(_directory, "legacy.db");
        CreateLegacyDatabase(path);

        using (var store = ClipStore.Open(path, _keys))
        {
            var clips = store.LoadAll();

            Assert.Single(clips);
            Assert.Equal("legacy-app", clips[0].SourceApp);
            Assert.Equal("legacy window title", clips[0].SourceTitle);
            Assert.Equal(string.Empty, clips[0].Nickname);

            // The nickname columns are added to pre-existing databases too.
            store.SetNickname(clips[0].Id, "legacy nickname");
            Assert.Equal("legacy nickname", store.Get(clips[0].Id)?.Nickname);

            // The migration recomputes the dedupe hash for the source-free rule,
            // so a later copy from another app still matches the old row.
            var again = store.Add(new ClipPayload { Text = "legacy text", SourceApp = "other-app" });

            Assert.False(again.IsNew);
            Assert.Equal("other-app", again.Clip.SourceApp);
            Assert.Single(store.LoadAll());
        }

        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_app, source_title FROM clips;";

        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(string.Empty, reader.GetString(0));
        Assert.Equal(string.Empty, reader.GetString(1));
        reader.Close();
        connection.Close();

        // Nothing readable may survive in the file or its WAL sidecar.
        var raw = File.ReadAllBytes(path);

        if (File.Exists(path + "-wal"))
        {
            raw = [.. raw, .. File.ReadAllBytes(path + "-wal")];
        }

        var text = System.Text.Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain("legacy window title", text, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy-app", text, StringComparison.Ordinal);
    }

    private void CreateLegacyDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();

        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = """
                CREATE TABLE clips (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind            INTEGER NOT NULL,
                    created_ms      INTEGER NOT NULL,
                    last_used_ms    INTEGER NOT NULL,
                    use_count       INTEGER NOT NULL DEFAULT 0,
                    bytes           INTEGER NOT NULL DEFAULT 0,
                    hash            BLOB    NOT NULL,
                    pinned          INTEGER NOT NULL DEFAULT 0,
                    preview_nonce   BLOB    NOT NULL,
                    preview_tag     BLOB    NOT NULL,
                    preview_cipher  BLOB    NOT NULL,
                    source_app      TEXT    NOT NULL DEFAULT '',
                    source_title    TEXT    NOT NULL DEFAULT '',
                    payload_nonce   BLOB    NOT NULL,
                    payload_tag     BLOB    NOT NULL,
                    payload_cipher  BLOB    NOT NULL
                );
                """;

            ddl.ExecuteNonQuery();
        }

        var payloadJson = JsonSerializer.Serialize(new { Text = "legacy text" });
        var sealedPayload = PayloadCipher.SealText(payloadJson, _keys.EncryptionKey);
        var sealedPreview = PayloadCipher.SealText("legacy text", _keys.EncryptionKey);

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO clips
                (kind, created_ms, last_used_ms, use_count, bytes, hash, pinned,
                 preview_nonce, preview_tag, preview_cipher,
                 source_app, source_title,
                 payload_nonce, payload_tag, payload_cipher)
            VALUES
                (0, $now, $now, 0, 22, $hash, 0,
                 $pnonce, $ptag, $pcipher,
                 $app, $title,
                 $nonce, $tag, $cipher);
            """;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue("$hash", new byte[32]);
        insert.Parameters.AddWithValue("$pnonce", sealedPreview.Nonce);
        insert.Parameters.AddWithValue("$ptag", sealedPreview.Tag);
        insert.Parameters.AddWithValue("$pcipher", sealedPreview.Ciphertext);
        insert.Parameters.AddWithValue("$app", "legacy-app");
        insert.Parameters.AddWithValue("$title", "legacy window title");
        insert.Parameters.AddWithValue("$nonce", sealedPayload.Nonce);
        insert.Parameters.AddWithValue("$tag", sealedPayload.Tag);
        insert.Parameters.AddWithValue("$cipher", sealedPayload.Ciphertext);

        insert.ExecuteNonQuery();
    }

    public void Dispose()
    {
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
