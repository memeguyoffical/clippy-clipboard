using System.Security.Cryptography;
using System.Text;

namespace Clippy.Core.Security;

/// <summary>
/// Derives the keys used for payload encryption and dedupe hashing.
/// The master key is random and stored only in DPAPI-protected form, so the
/// database is unreadable when copied to another machine or user account.
/// </summary>
public sealed class KeyRing : IDisposable
{
    private const string KeyFileName = "key.bin";

    // Bound into DPAPI protection so another app cannot unprotect the blob with
    // a plain CurrentUser call.
    private static readonly byte[] Entropy = "Clippy.KeyRing.v1"u8.ToArray();

    private KeyRing(byte[] master)
    {
        EncryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: null, info: "clippy.enc.v1"u8.ToArray());
        MacKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, salt: null, info: "clippy.mac.v1"u8.ToArray());

        // Lets the store prove that a database belongs to this key. It is a MAC,
        // so it cannot be ground back to the master.
        Fingerprint = HMACSHA256.HashData(MacKey, "clippy.fingerprint.v1"u8.ToArray());

        CryptographicOperations.ZeroMemory(master);
    }

    public byte[] EncryptionKey { get; }

    public byte[] MacKey { get; }

    public byte[] Fingerprint { get; }

    public static KeyRing LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, KeyFileName);

        if (File.Exists(path))
        {
            return Load(path);
        }

        var fresh = RandomNumberGenerator.GetBytes(32);
        var protectedBytes = ProtectedData.Protect(fresh, Entropy, DataProtectionScope.CurrentUser);

        // Unique name so two starting processes cannot overwrite each other's
        // half-written file. Move with overwrite:false is the atomic winner-pick.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, protectedBytes);

        try
        {
            File.Move(temporary, path, overwrite: false);
            return new KeyRing(fresh);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another process created the key first; use that one so both
            // processes share a key instead of orphaning each other's rows.
            CryptographicOperations.ZeroMemory(fresh);
            return Load(path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static KeyRing Load(string path)
    {
        var stored = File.ReadAllBytes(path);
        var master = ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
        return new KeyRing(master);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(EncryptionKey);
        CryptographicOperations.ZeroMemory(MacKey);
        CryptographicOperations.ZeroMemory(Fingerprint);
    }
}

/// <summary>AES-256-GCM output: nonce, tag and ciphertext travel together.</summary>
public readonly record struct SealedPayload(byte[] Nonce, byte[] Tag, byte[] Ciphertext);

public static class PayloadCipher
{
    public const int NonceSize = 12;
    public const int TagSize = 16;

    public static SealedPayload Seal(ReadOnlySpan<byte> plaintext, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return new SealedPayload(nonce, tag, ciphertext);
    }

    public static byte[] Open(SealedPayload sealedPayload, byte[] key)
    {
        var plaintext = new byte[sealedPayload.Ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedPayload.Nonce, sealedPayload.Ciphertext, sealedPayload.Tag, plaintext);

        return plaintext;
    }

    public static SealedPayload SealText(string value, byte[] key)
        => Seal(Encoding.UTF8.GetBytes(value), key);

    public static string OpenText(SealedPayload sealedPayload, byte[] key)
        => Encoding.UTF8.GetString(Open(sealedPayload, key));
}

/// <summary>
/// Keyed hash used to detect duplicate copies. Keyed so the stored digests
/// cannot be dictionary-attacked offline the way a bare SHA-256 would allow.
/// </summary>
public static class DedupeHasher
{
    public static byte[] Compute(string canonical, byte[] macKey)
        => HMACSHA256.HashData(macKey, Encoding.UTF8.GetBytes(canonical));
}
