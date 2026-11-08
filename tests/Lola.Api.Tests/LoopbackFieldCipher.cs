using Lola.Application.Security;

namespace Lola.Api.Tests;

/// Hermetic IFieldCipher for the API e2e (MPE-200): the real
/// TransitFieldCipher would hit OpenBao on every vault DEK wrap /
/// filename encrypt. The loopback keeps the `vault:` envelope shape
/// so ciphertext-at-rest assertions hold — plaintext is still never
/// stored where the fake "encrypts".
public sealed class LoopbackFieldCipher : IFieldCipher
{
    public Task<string> EncryptAsync(string plaintext,
        CancellationToken ct = default)
        => Task.FromResult($"vault:loop:{Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes(plaintext))}");

    public Task<IReadOnlyList<string>> EncryptAsync(
        IReadOnlyList<string> plaintexts,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(plaintexts
            .Select(p => $"vault:loop:{Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(p))}").ToList());

    public Task<string> DecryptAsync(string ciphertext,
        CancellationToken ct = default)
        => Task.FromResult(System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(ciphertext["vault:loop:".Length..])));

    public Task<IReadOnlyList<string>> DecryptAsync(
        IReadOnlyList<string> ciphertexts,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(ciphertexts
            .Select(c => System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(
                    c["vault:loop:".Length..]))).ToList());
}
