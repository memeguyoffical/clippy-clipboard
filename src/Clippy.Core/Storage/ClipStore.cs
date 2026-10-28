using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clippy.Core.Models;
using Clippy.Core.Search;
using Clippy.Core.Security;
using Microsoft.Data.Sqlite;

namespace Clippy.Core.Storage;

public sealed class StoredClip
{
    public required long Id { get; init; }
    public required ClipKind Kind { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastUsedAt { get; init; }
    public required int UseCount { get; init; }
    public required long Bytes { get; init; }
    public required bool Pinned { get; init; }
    public required string Preview { get; init; }
    public required string SourceApp { get; init; }
    public required string SourceTitle { get; init; }

    /// <summary>Encrypted user label, shown instead of the preview when set.</summary>
    public required string Nickname { get; init; }

    public SearchCandidate ToCandidate() => new(
        Id,
        Preview,
        SourceApp,
        Pinned,
        LastUsedAt.ToUnixTimeMilliseconds(),
        UseCount,
        Nickname);
}

public sealed record RetentionPolicy(int MaxEntries, int RetentionDays, long MaxBytes)
{
    public static RetentionPolicy Default { get; } = new(500, 30, 512L * 1024 * 1024);
}

public readonly record struct AddResult(StoredClip Clip, bool IsNew);

/// <summary>
/// Encrypted clipboard history in SQLite. One connection, one writer, guarded by
/// a lock: the capture path and the UI both touch it, and the volume is tiny.
/// </summary>
public sealed class ClipStore : IDisposable
{
    private const int PreviewLimit = 700;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SqliteConnection _connection;
    private readonly KeyRing _keys;
    private readonly Lock _gate = new();
    private bool _disposed;

    private ClipStore(SqliteConnection connection, KeyRing keys)
    {
        _connection = connection;
        _keys = keys;
    }

    public static ClipStore Open(string databasePath, KeyRing keys)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        try
        {
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, "PRAGMA synchronous=NORMAL;");
            Execute(connection, "PRAGMA secure_delete=ON;");
            Execute(connection, "PRAGMA foreign_keys=ON;");
            Execute(connection, Schema);
            EnsureKeyFingerprint(connection, keys, databasePath);
            MigrateSourceColumns(connection, keys);
            MigrateNicknameColumns(connection);

            return new ClipStore(connection, keys);
        }
        catch
        {
            // No store owns the connection yet, so close it before the caller
            // sees the error. Otherwise the file stays locked.
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Renames the database and its WAL sidecars out of the way. Used when the
    /// key no longer matches: the old history stays on disk for recovery, and
    /// the app can start with an empty database instead of failing forever.
    /// </summary>
    public static IReadOnlyList<string> MoveAside(string databasePath)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var moved = new List<string>();

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var source = databasePath + suffix;

            if (!File.Exists(source))
            {
                continue;
            }

            var destination = $"{source}.orphaned-{stamp}";
            File.Move(source, destination);
            moved.Add(destination);
        }

        return moved;
    }

    private const string FingerprintKey = "key_fingerprint";

    private static void EnsureKeyFingerprint(SqliteConnection connection, KeyRing keys, string databasePath)
    {
        var expected = Convert.ToHexString(keys.Fingerprint);

        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT value FROM meta WHERE key = $key;";
            read.Parameters.AddWithValue("$key", FingerprintKey);

            if (read.ExecuteScalar() is string stored)
            {
                if (!string.Equals(stored, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ClipStoreKeyException(databasePath);
                }

                return;
            }
        }

        // Databases written before the fingerprint column existed are adopted
        // when the key can actually decrypt one of their rows.
        if (HasClips(connection) && !CanReadAnyPreview(connection, keys))
        {
            throw new ClipStoreKeyException(databasePath);
        }

        using var write = connection.CreateCommand();
        write.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value);";
        write.Parameters.AddWithValue("$key", FingerprintKey);
        write.Parameters.AddWithValue("$value", expected);
        write.ExecuteNonQuery();
    }

    private static bool HasClips(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM clips);";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static bool CanReadAnyPreview(SqliteConnection connection, KeyRing keys)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT preview_nonce, preview_tag, preview_cipher FROM clips LIMIT 1;";

        using var reader = command.ExecuteReader();

        if (!reader.Read())
        {
            return true;
        }

        var sealedPreview = new SealedPayload(
            reader.GetFieldValue<byte[]>(0),
            reader.GetFieldValue<byte[]>(1),
            reader.GetFieldValue<byte[]>(2));

        try
        {
            PayloadCipher.OpenText(sealedPreview, keys.EncryptionKey);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS clips (
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
            source_nonce    BLOB    NOT NULL,
            source_tag      BLOB    NOT NULL,
            source_cipher   BLOB    NOT NULL,
            nickname_nonce  BLOB    NOT NULL DEFAULT X'',
            nickname_tag    BLOB    NOT NULL DEFAULT X'',
            nickname_cipher BLOB    NOT NULL DEFAULT X'',
            payload_nonce   BLOB    NOT NULL,
            payload_tag     BLOB    NOT NULL,
            payload_cipher  BLOB    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_clips_hash    ON clips(hash);
        CREATE INDEX IF NOT EXISTS idx_clips_created ON clips(created_ms);
        CREATE INDEX IF NOT EXISTS idx_clips_used    ON clips(last_used_ms);

        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    public AddResult Add(ClipPayload payload, string? previewOverride = null)
    {
        ThrowIfDisposed();

        var dto = ToDto(payload);
        var canonical = JsonSerializer.Serialize(dto, JsonOptions);

        // Dedupe ignores the source window: the same text copied from two apps is
        // one clip with the latest source, not two rows. The stored payload keeps
        // the source because it is encrypted anyway.
        var hash = DedupeHasher.Compute(DedupeCanonical(dto), _keys.MacKey);

        var preview = previewOverride ?? payload.SearchText;
        preview = Truncate(preview, PreviewLimit);

        lock (_gate)
        {
            var existingId = FindByHash(hash);

            if (existingId is not null)
            {
                try
                {
                    // The copy may come from another app this time; keep the
                    // source metadata current while reusing the row.
                    UpdateSource(existingId.Value, payload);
                    TouchCore(existingId.Value, DateTimeOffset.UtcNow);
                    var existing = GetCore(existingId.Value);

                    if (existing is not null)
                    {
                        return new AddResult(existing, IsNew: false);
                    }
                }
                catch (Exception ex) when (IsRowError(ex))
                {
                    // The duplicate row is unreadable. Drop it so the copy being
                    // captured now can be stored instead of failing forever.
                    DeleteCore(existingId.Value);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var sealedPayload = PayloadCipher.Seal(Encoding.UTF8.GetBytes(canonical), _keys.EncryptionKey);
            var sealedPreview = PayloadCipher.SealText(preview, _keys.EncryptionKey);
            var sealedSource = PayloadCipher.SealText(
                SourceJson(payload.SourceApp, payload.SourceTitle),
                _keys.EncryptionKey);

            using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO clips
                    (kind, created_ms, last_used_ms, use_count, bytes, hash, pinned,
                     preview_nonce, preview_tag, preview_cipher,
                     source_nonce, source_tag, source_cipher,
                     payload_nonce, payload_tag, payload_cipher)
                VALUES
                    ($kind, $created, $used, 0, $bytes, $hash, 0,
                     $pnonce, $ptag, $pcipher,
                     $snonce, $stag, $scipher,
                     $nonce, $tag, $cipher);
                SELECT last_insert_rowid();
                """;

            command.Parameters.AddWithValue("$kind", (int)payload.Kind);
            command.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$used", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$bytes", payload.EstimatedBytes);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$pnonce", sealedPreview.Nonce);
            command.Parameters.AddWithValue("$ptag", sealedPreview.Tag);
            command.Parameters.AddWithValue("$pcipher", sealedPreview.Ciphertext);
            command.Parameters.AddWithValue("$snonce", sealedSource.Nonce);
            command.Parameters.AddWithValue("$stag", sealedSource.Tag);
            command.Parameters.AddWithValue("$scipher", sealedSource.Ciphertext);
            command.Parameters.AddWithValue("$nonce", sealedPayload.Nonce);
            command.Parameters.AddWithValue("$tag", sealedPayload.Tag);
            command.Parameters.AddWithValue("$cipher", sealedPayload.Ciphertext);

            var id = (long)(command.ExecuteScalar() ?? 0L);

            return new AddResult(GetCore(id)!, IsNew: true);
        }
    }

    public IReadOnlyList<StoredClip> LoadAll(Action<long, Exception>? onCorruptRow = null)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT id, kind, created_ms, last_used_ms, use_count, bytes, pinned,
                       preview_nonce, preview_tag, preview_cipher,
                       source_nonce, source_tag, source_cipher,
                       nickname_nonce, nickname_tag, nickname_cipher
                FROM clips
                ORDER BY last_used_ms DESC;
                """;

            var results = new List<StoredClip>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                // A single unreadable row (bad key epoch, torn write, corrupted
                // page) must not hide the rest of the history.
                try
                {
                    results.Add(ReadMetadata(reader));
                }
                catch (Exception ex) when (IsRowError(ex))
                {
                    onCorruptRow?.Invoke(reader.GetInt64(0), ex);
                }
            }

            return results;
        }
    }

    public ClipPayload? LoadPayload(long id)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT payload_nonce, payload_tag, payload_cipher FROM clips WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();

            if (!reader.Read())
            {
                return null;
            }

            var sealedPayload = new SealedPayload(
                reader.GetFieldValue<byte[]>(0),
                reader.GetFieldValue<byte[]>(1),
                reader.GetFieldValue<byte[]>(2));

            try
            {
                var json = PayloadCipher.OpenText(sealedPayload, _keys.EncryptionKey);
                var dto = JsonSerializer.Deserialize<PayloadDto>(json, JsonOptions);

                return dto is null ? null : FromDto(dto);
            }
            catch (Exception ex) when (IsRowError(ex) || ex is JsonException)
            {
                // The row cannot be read; callers treat it as missing instead of
                // taking down the popup.
                return null;
            }
        }
    }

    public void Touch(long id)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            TouchCore(id, DateTimeOffset.UtcNow);
        }
    }

    public StoredClip? Get(long id)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            return GetCore(id);
        }
    }

    public void SetPinned(long id, bool pinned)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE clips SET pinned = $pinned WHERE id = $id;";
            command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Sets the encrypted label for a clip. An empty value removes it; the
    /// columns are blanked rather than left holding an encrypted empty string.
    /// </summary>
    public void SetNickname(long id, string? nickname)
    {
        ThrowIfDisposed();

        var value = Truncate((nickname ?? string.Empty).Trim(), 80);

        lock (_gate)
        {
            using var command = _connection.CreateCommand();

            if (value.Length == 0)
            {
                command.CommandText = """
                    UPDATE clips
                    SET nickname_nonce = X'', nickname_tag = X'', nickname_cipher = X''
                    WHERE id = $id;
                    """;
            }
            else
            {
                var sealedNickname = PayloadCipher.SealText(value, _keys.EncryptionKey);
                command.CommandText = """
                    UPDATE clips
                    SET nickname_nonce = $nonce,
                        nickname_tag = $tag,
                        nickname_cipher = $cipher
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$nonce", sealedNickname.Nonce);
                command.Parameters.AddWithValue("$tag", sealedNickname.Tag);
                command.Parameters.AddWithValue("$cipher", sealedNickname.Ciphertext);
            }

            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    public void Delete(long id)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            DeleteCore(id);
        }
    }

    private void DeleteCore(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM clips WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private void UpdateSource(long id, ClipPayload payload)
    {
        var sealedSource = PayloadCipher.SealText(
            SourceJson(payload.SourceApp, payload.SourceTitle),
            _keys.EncryptionKey);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE clips
            SET source_nonce = $nonce, source_tag = $tag, source_cipher = $cipher
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$nonce", sealedSource.Nonce);
        command.Parameters.AddWithValue("$tag", sealedSource.Tag);
        command.Parameters.AddWithValue("$cipher", sealedSource.Ciphertext);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Clear(bool includePinned)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = includePinned
                    ? "DELETE FROM clips;"
                    : "DELETE FROM clips WHERE pinned = 0;";
                command.ExecuteNonQuery();
            }

            // secure_delete zeroes pages, but VACUUM is what actually reclaims them.
            Execute(_connection, "VACUUM;");
        }
    }

    public (int Count, long Bytes) GetUsage()
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), COALESCE(SUM(bytes), 0) FROM clips;";

            using var reader = command.ExecuteReader();
            reader.Read();

            return (reader.GetInt32(0), reader.GetInt64(1));
        }
    }

    /// <summary>
    /// Applies retention. Pinned clips are never removed by any of the three
    /// limits — they are deleted only by an explicit user action.
    /// </summary>
    public int Prune(RetentionPolicy policy, DateTimeOffset now)
    {
        ThrowIfDisposed();

        lock (_gate)
        {
            var removed = 0;

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "DELETE FROM clips WHERE pinned = 0 AND created_ms < $cutoff;";
                command.Parameters.AddWithValue("$cutoff", now.AddDays(-policy.RetentionDays).ToUnixTimeMilliseconds());
                removed += command.ExecuteNonQuery();
            }

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = """
                    DELETE FROM clips
                    WHERE pinned = 0
                      AND id NOT IN (
                          SELECT id FROM clips WHERE pinned = 0
                          ORDER BY last_used_ms DESC
                          LIMIT $max
                      );
                    """;
                command.Parameters.AddWithValue("$max", Math.Max(1, policy.MaxEntries));
                removed += command.ExecuteNonQuery();
            }

            removed += TrimToByteBudget(policy.MaxBytes);

            if (removed >= 100)
            {
                // secure_delete zeroes the rows, but only VACUUM gives the space
                // back. Doing this on every capture would be far too expensive.
                Execute(_connection, "VACUUM;");
            }

            return removed;
        }
    }

    private int TrimToByteBudget(long maxBytes)
    {
        long total;

        using (var totalCommand = _connection.CreateCommand())
        {
            // Pinned clips are exempt, so their bytes must not push unpinned
            // clips out of the budget.
            totalCommand.CommandText = "SELECT COALESCE(SUM(bytes), 0) FROM clips WHERE pinned = 0;";
            total = (long)(totalCommand.ExecuteScalar() ?? 0L);
        }

        if (total <= maxBytes)
        {
            return 0;
        }

        var victims = new List<long>();

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, bytes FROM clips WHERE pinned = 0 ORDER BY last_used_ms ASC;
                """;

            using var reader = command.ExecuteReader();

            while (total > maxBytes && reader.Read())
            {
                var id = reader.GetInt64(0);
                victims.Add(id);
                total -= reader.GetInt64(1);
            }
        }

        if (victims.Count == 0)
        {
            // Everything left is pinned. Honouring "pinned never expires" matters
            // more than the byte cap, so we stop and let the UI warn instead.
            return 0;
        }

        DeleteMany(victims);
        return victims.Count;
    }

    private void DeleteMany(IReadOnlyList<long> ids)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM clips WHERE id = $id;";
        var parameter = command.Parameters.Add("$id", SqliteType.Integer);

        foreach (var id in ids)
        {
            parameter.Value = id;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private long? FindByHash(byte[] hash)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id FROM clips WHERE hash = $hash ORDER BY created_ms DESC LIMIT 1;";
        command.Parameters.AddWithValue("$hash", hash);

        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : (long)result;
    }

    private void TouchCore(long id, DateTimeOffset now)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE clips SET last_used_ms = $used, use_count = use_count + 1 WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$used", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private StoredClip? GetCore(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, created_ms, last_used_ms, use_count, bytes, pinned,
                   preview_nonce, preview_tag, preview_cipher,
                   source_nonce, source_tag, source_cipher,
                   nickname_nonce, nickname_tag, nickname_cipher
            FROM clips WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMetadata(reader) : null;
    }

    private StoredClip ReadMetadata(SqliteDataReader reader)
    {
        var sealedPreview = new SealedPayload(
            reader.GetFieldValue<byte[]>(7),
            reader.GetFieldValue<byte[]>(8),
            reader.GetFieldValue<byte[]>(9));

        var (sourceApp, sourceTitle) = ReadSource(reader);

        return new StoredClip
        {
            Id = reader.GetInt64(0),
            Kind = (ClipKind)reader.GetInt32(1),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            LastUsedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
            UseCount = reader.GetInt32(4),
            Bytes = reader.GetInt64(5),
            Pinned = reader.GetInt32(6) != 0,
            Preview = PayloadCipher.OpenText(sealedPreview, _keys.EncryptionKey),
            SourceApp = sourceApp,
            SourceTitle = sourceTitle,
            Nickname = ReadNickname(reader),
        };
    }

    /// <summary>
    /// The nickname decrypts on its own so a damaged label cannot hide the clip
    /// it belongs to.
    /// </summary>
    private string ReadNickname(SqliteDataReader reader)
    {
        var sealedNickname = new SealedPayload(
            reader.GetFieldValue<byte[]>(13),
            reader.GetFieldValue<byte[]>(14),
            reader.GetFieldValue<byte[]>(15));

        if (sealedNickname.Ciphertext.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return PayloadCipher.OpenText(sealedNickname, _keys.EncryptionKey);
        }
        catch (Exception ex) when (IsRowError(ex))
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Source metadata decrypts separately from the preview. A row whose source
    /// blob is damaged still shows its content instead of disappearing.
    /// </summary>
    private (string App, string Title) ReadSource(SqliteDataReader reader)
    {
        var sealedSource = new SealedPayload(
            reader.GetFieldValue<byte[]>(10),
            reader.GetFieldValue<byte[]>(11),
            reader.GetFieldValue<byte[]>(12));

        if (sealedSource.Ciphertext.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            var json = PayloadCipher.OpenText(sealedSource, _keys.EncryptionKey);
            var source = JsonSerializer.Deserialize<SourceDto>(json, JsonOptions);

            return (source?.App ?? string.Empty, source?.Title ?? string.Empty);
        }
        catch (Exception ex) when (IsRowError(ex) || ex is JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    private static string Truncate(string value, int limit)
        => value.Length <= limit ? value : value[..limit];

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static PayloadDto ToDto(ClipPayload payload) => new()
    {
        Text = payload.Text,
        Html = payload.Html,
        Rtf = payload.Rtf,
        ImagePng = payload.ImagePng,
        Files = payload.Files,
        SourceApp = payload.SourceApp,
        SourceTitle = payload.SourceTitle,
    };

    private static ClipPayload FromDto(PayloadDto dto) => new()
    {
        Text = dto.Text,
        Html = dto.Html,
        Rtf = dto.Rtf,
        ImagePng = dto.ImagePng,
        Files = dto.Files,
        SourceApp = dto.SourceApp,
        SourceTitle = dto.SourceTitle,
    };

    /// <summary>
    /// Databases from before source encryption keep their plaintext source
    /// columns until this runs. Values are re-sealed per row, the hash is
    /// recomputed for the source-free dedupe rule, then the old text is blanked.
    /// </summary>
    private static void MigrateSourceColumns(SqliteConnection connection, KeyRing keys)
    {
        if (ColumnExists(connection, "clips", "source_cipher"))
        {
            return;
        }

        Execute(connection, "ALTER TABLE clips ADD COLUMN source_nonce BLOB NOT NULL DEFAULT X'';");
        Execute(connection, "ALTER TABLE clips ADD COLUMN source_tag BLOB NOT NULL DEFAULT X'';");
        Execute(connection, "ALTER TABLE clips ADD COLUMN source_cipher BLOB NOT NULL DEFAULT X'';");

        var rows = new List<LegacyRow>();

        using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT id, source_app, source_title, hash,
                       payload_nonce, payload_tag, payload_cipher
                FROM clips;
                """;

            using var reader = read.ExecuteReader();

            while (reader.Read())
            {
                rows.Add(new LegacyRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetFieldValue<byte[]>(3),
                    reader.GetFieldValue<byte[]>(4),
                    reader.GetFieldValue<byte[]>(5),
                    reader.GetFieldValue<byte[]>(6)));
            }
        }

        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE clips
            SET source_nonce = $nonce,
                source_tag = $tag,
                source_cipher = $cipher,
                hash = $hash
            WHERE id = $id;
            """;

        var nonce = update.Parameters.Add("$nonce", SqliteType.Blob);
        var tag = update.Parameters.Add("$tag", SqliteType.Blob);
        var cipher = update.Parameters.Add("$cipher", SqliteType.Blob);
        var hashParam = update.Parameters.Add("$hash", SqliteType.Blob);
        var id = update.Parameters.Add("$id", SqliteType.Integer);

        foreach (var row in rows)
        {
            var app = row.App;
            var title = row.Title;
            var hash = row.Hash;

            try
            {
                var payloadJson = PayloadCipher.OpenText(
                    new SealedPayload(row.PayloadNonce, row.PayloadTag, row.PayloadCipher),
                    keys.EncryptionKey);

                if (JsonSerializer.Deserialize<PayloadDto>(payloadJson, JsonOptions) is { } dto)
                {
                    if (string.IsNullOrEmpty(app))
                    {
                        app = dto.SourceApp ?? string.Empty;
                    }

                    if (string.IsNullOrEmpty(title))
                    {
                        title = dto.SourceTitle ?? string.Empty;
                    }

                    hash = DedupeHasher.Compute(DedupeCanonical(dto), keys.MacKey);
                }
            }
            catch (Exception ex) when (IsRowError(ex))
            {
                // Unreadable payload: still seal the plaintext source so it does
                // not stay in the clear, and keep the old hash.
            }

            var sealedSource = PayloadCipher.SealText(SourceJson(app, title), keys.EncryptionKey);

            nonce.Value = sealedSource.Nonce;
            tag.Value = sealedSource.Tag;
            cipher.Value = sealedSource.Ciphertext;
            hashParam.Value = hash;
            id.Value = row.Id;

            update.ExecuteNonQuery();
        }

        transaction.Commit();

        // Old plaintext copies are no longer needed. secure_delete overwrites the
        // cells as they are cleared.
        Execute(connection, "UPDATE clips SET source_app = '', source_title = '';");

        if (rows.Count > 0)
        {
            // Cleared values can still sit in pages freed by earlier prunes.
            // VACUUM rewrites the file so no plaintext survives the migration.
            Execute(connection, "VACUUM;");
        }
    }

    /// <summary>
    /// Adds the encrypted nickname columns to databases created before the
    /// feature. Purely additive: no row rewrite and no plaintext ever existed.
    /// </summary>
    private static void MigrateNicknameColumns(SqliteConnection connection)
    {
        if (ColumnExists(connection, "clips", "nickname_cipher"))
        {
            return;
        }

        Execute(connection, "ALTER TABLE clips ADD COLUMN nickname_nonce BLOB NOT NULL DEFAULT X'';");
        Execute(connection, "ALTER TABLE clips ADD COLUMN nickname_tag BLOB NOT NULL DEFAULT X'';");
        Execute(connection, "ALTER TABLE clips ADD COLUMN nickname_cipher BLOB NOT NULL DEFAULT X'';");
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string SourceJson(string? app, string? title) =>
        JsonSerializer.Serialize(
            new SourceDto(app ?? string.Empty, title ?? string.Empty),
            JsonOptions);

    private static string DedupeCanonical(PayloadDto dto) =>
        JsonSerializer.Serialize(
            new PayloadDto
            {
                Text = dto.Text,
                Html = dto.Html,
                Rtf = dto.Rtf,
                ImagePng = dto.ImagePng,
                Files = dto.Files,
            },
            JsonOptions);

    private static bool IsRowError(Exception exception) =>
        exception is CryptographicException
            or FormatException
            or InvalidCastException
            or ArgumentException
            or OverflowException
            or JsonException;

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }

    private sealed class PayloadDto
    {
        public string? Text { get; set; }
        public string? Html { get; set; }
        public string? Rtf { get; set; }
        public byte[]? ImagePng { get; set; }
        public string[]? Files { get; set; }
        public string? SourceApp { get; set; }
        public string? SourceTitle { get; set; }
    }

    private sealed record SourceDto(string App, string Title);

    private sealed record LegacyRow(
        long Id,
        string App,
        string Title,
        byte[] Hash,
        byte[] PayloadNonce,
        byte[] PayloadTag,
        byte[] PayloadCipher);
}
