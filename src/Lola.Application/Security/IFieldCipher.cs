namespace Lola.Application.Security;

/// Field-level PII encryption for the current tenant
/// (<see cref="CipherContext"/>). Implementations talk to OpenBao
/// transit — ciphertext carries the `vault:vN:` version envelope so
/// rotation/rewrap metadata is preserved. Repositories never see
/// plaintext outside this boundary (ADR-0002/-0004).
public interface IFieldCipher
{
    /// Plaintext → `vault:vN:` ciphertext for the current tenant's key.
    Task<string> EncryptAsync(string plaintext, CancellationToken ct = default);

    /// Batch form — one transit round-trip via `batch_input`.
    Task<IReadOnlyList<string>> EncryptAsync(IReadOnlyList<string> plaintexts, CancellationToken ct = default);

    /// `vault:vN:` ciphertext → plaintext. Throws if the key version is
    /// gone (e.g. shredded) — that is crypto-erasure working as designed.
    Task<string> DecryptAsync(string ciphertext, CancellationToken ct = default);

    /// Batch form — one transit round-trip via `batch_input`.
    Task<IReadOnlyList<string>> DecryptAsync(IReadOnlyList<string> ciphertexts, CancellationToken ct = default);
}
