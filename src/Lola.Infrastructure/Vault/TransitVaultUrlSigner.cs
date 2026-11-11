using System.Security.Cryptography;
using System.Text;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
using Lola.Infrastructure.Crypto;
using Lola.Infrastructure.OpenBao;

namespace Lola.Infrastructure.Vault;

/// Real download-URL signer (MPE-202): transit HMAC-SHA256 under the
/// tenant's `tenant-<id>-idx` hmac key — the same key that backs blind
/// indexes, created at provisioning and destroyed on erasure. A minted
/// URL therefore dies with the tenant's keys (invariant 9).
///
/// Payload: `v1|{tid}|{docId}|{expUnix}` — the tenant id is INSIDE the
/// signed string even though the key is already tenant-scoped, so a
/// signature can never be re-anchored to a different tenant. `sig` is
/// the full transit token (`vault:vN:<b64>`) — verify recomputes and
/// compares fixed-time; expiry is checked before any crypto so a stale
/// URL is refused on the clock alone.
public sealed class TransitVaultUrlSigner(
    OpenBaoTransitClient transit, TimeProvider clock) : IVaultUrlSigner
{
    internal static string Payload(string tenantId, string docId,
        DateTimeOffset expUtc)
        => $"v1|{tenantId}|{docId}|{expUtc.ToUnixTimeSeconds()}";

    public Task<string> SignAsync(string docId, DateTimeOffset expUtc,
        CancellationToken ct = default)
    {
        var tid = TenantContext.Required.TenantId;
        return transit.HmacAsync(TransitKeyManager.IndexKey(tid),
            Payload(tid, docId, expUtc), ct);
    }

    public async Task<bool> VerifyAsync(string docId,
        DateTimeOffset expUtc, string signature,
        CancellationToken ct = default)
    {
        if (expUtc <= clock.GetUtcNow()) return false;
        try
        {
            var expected = await SignAsync(docId, expUtc, ct);
            var a = Encoding.ASCII.GetBytes(expected);
            var b = Encoding.ASCII.GetBytes(signature);
            // Lengths are public (HMAC output size); the compare itself
            // is fixed-time so a tamper oracle can't measure position.
            return a.Length == b.Length
                && CryptographicOperations.FixedTimeEquals(a, b);
        }
        catch
        {
            // Transit hiccup or malformed token — never verify.
            return false;
        }
    }
}
