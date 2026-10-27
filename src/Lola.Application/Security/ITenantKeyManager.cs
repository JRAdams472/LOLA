namespace Lola.Application.Security;

/// Lifecycle for a tenant's transit keys: the data-encryption key
/// (`tenant-<id>`) and the blind-index key (`tenant-<id>-idx`).
public interface ITenantKeyManager
{
    /// Create both tenant keys if absent (idempotent — safe at
    /// provisioning time and as a lazy guard).
    Task EnsureKeysAsync(string tenantId, CancellationToken ct = default);

    /// Rotate the tenant data key — new ciphertext uses the latest
    /// version; old versions keep decrypting.
    Task RotateAsync(string tenantId, CancellationToken ct = default);

    /// Rewrap one ciphertext to the latest key version inside OpenBao —
    /// plaintext never transits the wire.
    Task<string> RewrapAsync(string tenantId, string ciphertext, CancellationToken ct = default);

    /// Batch rewrap for migration sweeps.
    Task<IReadOnlyList<string>> RewrapAsync(string tenantId, IReadOnlyList<string> ciphertexts, CancellationToken ct = default);

    /// Crypto-shredding: two-step (deletion_allowed → delete) on BOTH
    /// tenant keys. After this returns, every ciphertext and blind index
    /// for the tenant is permanently unrecoverable.
    Task DestroyAsync(string tenantId, CancellationToken ct = default);
}
