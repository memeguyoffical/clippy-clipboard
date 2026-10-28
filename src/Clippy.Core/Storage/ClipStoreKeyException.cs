namespace Clippy.Core.Storage;

/// <summary>
/// Raised when the history database cannot be decrypted with the key on disk.
/// The database is left untouched so it can be recovered or kept for later;
/// startup decides whether to move it aside.
/// </summary>
public sealed class ClipStoreKeyException : Exception
{
    public ClipStoreKeyException(string databasePath, Exception? inner = null)
        : base(
            $"The history database at '{databasePath}' cannot be decrypted with the current key. " +
            "The key file is missing, replaced, or belongs to another Windows account.",
            inner)
    {
        DatabasePath = databasePath;
    }

    public string DatabasePath { get; }
}
