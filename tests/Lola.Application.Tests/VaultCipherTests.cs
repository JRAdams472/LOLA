using System.Security.Cryptography;
using System.Text;
using Lola.Application.Security;
using Lola.Application.Vault;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-200 cipher acceptance: chunked AEAD round-trips arbitrary
/// plaintext, the DEK arrives back transit-wrapped, every tamper
/// class fails closed on open (tag verify, truncation, wrong DEK),
/// and the size cap aborts mid-stream.
public sealed class VaultCipherTests
{
    private sealed class LoopbackCipher : IFieldCipher
    {
        public Task<string> EncryptAsync(string plaintext,
            CancellationToken ct = default)
            => Task.FromResult($"vault:test:{plaintext}");
        public Task<IReadOnlyList<string>> EncryptAsync(
            IReadOnlyList<string> plaintexts,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(
                plaintexts.Select(p => $"vault:test:{p}").ToList());
        public Task<string> DecryptAsync(string ciphertext,
            CancellationToken ct = default)
            => Task.FromResult(ciphertext["vault:test:".Length..]);
        public Task<IReadOnlyList<string>> DecryptAsync(
            IReadOnlyList<string> ciphertexts,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(
                ciphertexts.Select(c => c["vault:test:".Length..])
                    .ToList());
    }

    private static readonly VaultCipher Cipher = new(new LoopbackCipher());

    private static async Task<(byte[] ct, string dek, string sha, long len)>
        SealAsync(byte[] plaintext, long max = 1L << 30, int chunk = 64)
    {
        await using var sealing = Cipher.Seal(
            new MemoryStream(plaintext), max, chunk);
        await using var ms = new MemoryStream();
        await sealing.CopyToAsync(ms);
        return (ms.ToArray(), sealing.WrappedDek,
            sealing.Sha256Hex, sealing.PlaintextLength);
    }

    private static async Task<byte[]> OpenAsync(byte[] ct, string dek)
    {
        await using var opening = await Cipher.OpenAsync(
            dek, new MemoryStream(ct));
        await using var ms = new MemoryStream();
        await opening.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Round_trip_restores_bytes_and_reports_metadata()
    {
        var rng = new Random(42);
        var plaintext = new byte[10_000];
        rng.NextBytes(plaintext);

        var (ct, dek, sha, len) = await SealAsync(plaintext);

        ct.ShouldNotBe(plaintext);
        dek.ShouldStartWith("vault:test:");
        sha.ShouldBe(Convert.ToHexStringLower(
            SHA256.HashData(plaintext)));
        len.ShouldBe(plaintext.Length);
        (await OpenAsync(ct, dek)).ShouldBe(plaintext);
    }

    [Fact]
    public async Task Empty_input_seals_and_opens_to_empty()
    {
        var (ct, dek, _, len) = await SealAsync([]);
        len.ShouldBe(0);
        (await OpenAsync(ct, dek)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Tampered_ciphertext_fails_tag_verification()
    {
        var (ct, dek, _, _) = await SealAsync(
            Encoding.UTF8.GetBytes("paystub contents"));
        ct[^20] ^= 0xFF;              // flip inside the last tag
        await Should.ThrowAsync<CryptographicException>(
            () => OpenAsync(ct, dek));
    }

    [Fact]
    public async Task Truncated_object_is_detected()
    {
        var (ct, dek, _, _) = await SealAsync(
            Encoding.UTF8.GetBytes("w2 contents"));
        var truncated = ct[..(ct.Length - 8)];   // drop terminator
        await Should.ThrowAsync<CryptographicException>(
            () => OpenAsync(truncated, dek));
    }

    [Fact]
    public async Task Wrong_wrapped_dek_cannot_open()
    {
        var other = await SealAsync(Encoding.UTF8.GetBytes("other"));
        var (ct, _, _, _) = await SealAsync(
            Encoding.UTF8.GetBytes("mine"));
        // A DEK from a different seal opens the stream but fails at
        // the first tag — ciphertext can't be replayed under another
        // key's unwrap.
        await Should.ThrowAsync<CryptographicException>(
            () => OpenAsync(ct, other.dek));
    }

    [Fact]
    public async Task Size_cap_aborts_mid_stream()
    {
        var plaintext = new byte[1024];
        await Should.ThrowAsync<VaultTooLargeException>(
            () => SealAsync(plaintext, max: 512));
    }
}
