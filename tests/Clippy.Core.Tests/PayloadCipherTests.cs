using System.Security.Cryptography;
using System.Text;
using Clippy.Core.Security;

namespace Clippy.Core.Tests;

public sealed class PayloadCipherTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(static i => (byte)i).ToArray();

    [Fact]
    public void Round_trips_text()
    {
        const string secret = "copied password-ish text ☕";

        var sealedPayload = PayloadCipher.SealText(secret, Key);
        var opened = PayloadCipher.OpenText(sealedPayload, Key);

        Assert.Equal(secret, opened);
    }

    [Fact]
    public void Ciphertext_does_not_contain_the_plaintext()
    {
        const string secret = "unique-marker-9f3a";

        var sealedPayload = PayloadCipher.SealText(secret, Key);
        var asText = Encoding.UTF8.GetString(sealedPayload.Ciphertext);

        Assert.DoesNotContain("unique-marker", asText, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_seal_uses_a_fresh_nonce()
    {
        var first = PayloadCipher.SealText("same", Key);
        var second = PayloadCipher.SealText("same", Key);

        Assert.NotEqual(first.Nonce, second.Nonce);
    }

    [Fact]
    public void Tampered_tag_is_rejected()
    {
        var sealedPayload = PayloadCipher.SealText("integrity", Key);
        sealedPayload.Tag[0] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(
            () => PayloadCipher.Open(sealedPayload, Key));
    }

    [Fact]
    public void Wrong_key_is_rejected()
    {
        var sealedPayload = PayloadCipher.SealText("integrity", Key);
        var wrongKey = RandomNumberGenerator.GetBytes(32);

        Assert.Throws<AuthenticationTagMismatchException>(
            () => PayloadCipher.Open(sealedPayload, wrongKey));
    }

    [Fact]
    public void Dedupe_hash_is_key_dependent()
    {
        var keyA = RandomNumberGenerator.GetBytes(32);
        var keyB = RandomNumberGenerator.GetBytes(32);

        var hashA = DedupeHasher.Compute("same text", keyA);
        var hashB = DedupeHasher.Compute("same text", keyB);

        Assert.NotEqual(hashA, hashB);
        Assert.Equal(hashA, DedupeHasher.Compute("same text", keyA));
    }
}
