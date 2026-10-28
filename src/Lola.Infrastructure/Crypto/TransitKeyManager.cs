using Lola.Application.Security;
using Lola.Domain.Tenancy;
using Lola.Infrastructure.OpenBao;

namespace Lola.Infrastructure.Crypto;

/// <see cref="ITenantKeyManager"/> over transit. Both tenant keys are
/// managed together — an intact index key over a shredded DEK (or vice
/// versa) would leak searchability for data that's supposed to be gone.
public sealed class TransitKeyManager(OpenBaoTransitClient transit) : ITenantKeyManager
{
    // TenantId.Validate runs at the key-name choke point — an unsafe
    // tenant id can never reach a transit key path (M2 hardening F1).
    private static string DataKey(string tenantId) => $"tenant-{TenantId.Validate(tenantId)}";
    private static string IndexKey(string tenantId) => $"tenant-{tenantId}-idx";

    public async Task EnsureKeysAsync(string tenantId, CancellationToken ct = default)
    {
        await transit.EnsureKeyAsync(DataKey(tenantId), "aes256-gcm96", ct: ct);
        // hmac keys require an explicit key_size — 32 bytes = HMAC-256.
        await transit.EnsureKeyAsync(IndexKey(tenantId), "hmac", 32, ct);
    }

    public Task RotateAsync(string tenantId, CancellationToken ct = default) =>
        transit.RotateKeyAsync(DataKey(tenantId), ct);

    public Task<string> RewrapAsync(string tenantId, string ciphertext, CancellationToken ct = default) =>
        transit.RewrapAsync(DataKey(tenantId), ciphertext, ct);

    public Task<IReadOnlyList<string>> RewrapAsync(string tenantId, IReadOnlyList<string> ciphertexts, CancellationToken ct = default) =>
        transit.RewrapAsync(DataKey(tenantId), ciphertexts, ct);

    /// Crypto-shredding: deletion_allowed → delete, on BOTH keys.
    /// Irreversible — after this every tenant ciphertext and blind index
    /// is permanently unrecoverable. Retention checks are the caller's
    /// job before invoking (Reg B 25-month, invariant 9). Idempotent:
    /// an already-destroyed key reads as absent and is skipped, so a
    /// retried erasure converges instead of dying on 404s (MPE-107).
    public async Task DestroyAsync(string tenantId, CancellationToken ct = default)
    {
        foreach (var key in new[] { DataKey(tenantId), IndexKey(tenantId) })
        {
            if (!await transit.KeyExistsAsync(key, ct)) continue;
            await transit.SetDeletionAllowedAsync(key, ct);
            await transit.DeleteKeyAsync(key, ct);
        }
    }
}
