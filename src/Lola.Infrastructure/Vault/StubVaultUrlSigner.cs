using System.Security.Cryptography;
using System.Text;
using Lola.Application.Tenancy;
using Lola.Application.Vault;

namespace Lola.Infrastructure.Vault;

/// Hermetic URL signer (MPE-202): local HMAC-SHA256 with a fixed
/// in-process key — the same sign/verify contract as
/// <see cref="TransitVaultUrlSigner"/> (payload shape, expiry check,
/// fixed-time compare) without OpenBao, so the capability route is
/// exercisable under VAULT_ADAPTER=stub and in hermetic tests. The
/// key dies with the process, exactly like the stub vault's objects.
public sealed class StubVaultUrlSigner(TimeProvider clock) : IVaultUrlSigner
{
    private static readonly byte[] Key =
        SHA256.HashData("lola-vault-stub-signer"u8);

    public Task<string> SignAsync(string docId, DateTimeOffset expUtc,
        CancellationToken ct = default)
    {
        var tid = TenantContext.Required.TenantId;
        var mac = HMACSHA256.HashData(Key, Encoding.ASCII.GetBytes(
            TransitVaultUrlSigner.Payload(tid, docId, expUtc)));
        return Task.FromResult("stub:v1:" + Convert.ToBase64String(mac));
    }

    public async Task<bool> VerifyAsync(string docId,
        DateTimeOffset expUtc, string signature,
        CancellationToken ct = default)
    {
        if (expUtc <= clock.GetUtcNow()) return false;
        var expected = await SignAsync(docId, expUtc, ct);
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(signature);
        return a.Length == b.Length
            && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
