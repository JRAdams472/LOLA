using Lola.Application.Security;
using Lola.Domain.Classification;

namespace Lola.DataPlane.Tests;

/// Hermetic crypto fakes for control-plane tests: the cipher keeps the
/// `vault:` envelope shape so ciphertext-at-rest assertions hold, and
/// the index is a deterministic keyed token â€” write and lookup use the
/// same instance so equality holds without OpenBao.
public sealed class TestFieldCipher : IFieldCipher
{
    public Task<string> EncryptAsync(string plaintext, CancellationToken ct = default)
        => Task.FromResult($"vault:loop:{Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(plaintext))}");

    public Task<IReadOnlyList<string>> EncryptAsync(
        IReadOnlyList<string> plaintexts, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(plaintexts
            .Select(p => $"vault:loop:{Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(p))}").ToList());

    public Task<string> DecryptAsync(string ciphertext, CancellationToken ct = default)
        => Task.FromResult(System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(ciphertext["vault:loop:".Length..])));

    public Task<IReadOnlyList<string>> DecryptAsync(
        IReadOnlyList<string> ciphertexts, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(ciphertexts
            .Select(c => System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(c["vault:loop:".Length..]))).ToList());
}

public sealed class TestBlindIndex : IBlindIndex
{
    public Task<string> IndexAsync(PiiClass classification, string value,
        CancellationToken ct = default)
        => Task.FromResult($"idx:loop:{Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()))}");
}

public static class TestCrypto
{
    public static readonly IFieldCipher Cipher = new TestFieldCipher();
    public static readonly IBlindIndex Index = new TestBlindIndex();

    /// Synchronous shapes matching the fakes — raw INSERT fixtures
    /// write these directly so the stored value is real ciphertext/idx
    /// form, never a plaintext marker.
    public static string Enc(string value) => $"vault:loop:{Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes(value))}";

    public static string Idx(string value) => $"idx:loop:{Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes(value.Trim().ToLowerInvariant()))}";
}
