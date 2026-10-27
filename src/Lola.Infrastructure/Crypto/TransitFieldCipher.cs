using Lola.Application.Security;
using Lola.Infrastructure.OpenBao;

namespace Lola.Infrastructure.Crypto;

/// <see cref="IFieldCipher"/> over OpenBao transit. The tenant key
/// (`tenant-<id>`) comes from <see cref="CipherContext"/> — no tenant,
/// no crypto. Every call is a transit round-trip: tenant key material
/// never enters API memory.
public sealed class TransitFieldCipher(OpenBaoTransitClient transit) : IFieldCipher
{
    private static string KeyName => $"tenant-{CipherContext.RequiredTenantId}";

    public Task<string> EncryptAsync(string plaintext, CancellationToken ct = default) =>
        transit.EncryptAsync(KeyName, plaintext, ct);

    public Task<IReadOnlyList<string>> EncryptAsync(IReadOnlyList<string> plaintexts, CancellationToken ct = default) =>
        transit.EncryptAsync(KeyName, plaintexts, ct);

    public Task<string> DecryptAsync(string ciphertext, CancellationToken ct = default) =>
        transit.DecryptAsync(KeyName, ciphertext, ct);

    public Task<IReadOnlyList<string>> DecryptAsync(IReadOnlyList<string> ciphertexts, CancellationToken ct = default) =>
        transit.DecryptAsync(KeyName, ciphertexts, ct);
}
