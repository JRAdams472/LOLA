using Lola.Application.Security;
using Lola.Domain.Classification;
using Lola.Infrastructure.OpenBao;

namespace Lola.Infrastructure.Crypto;

/// <see cref="IBlindIndex"/> over a dedicated `hmac`-type transit key
/// (`tenant-<id>-idx`) — an index key can compute HMACs but can never
/// decrypt. Tokens are stored as `idx:vN:<hmac>`: the version tag makes
/// idx-key rotation visible so a reindex sweep knows what to rewrite.
public sealed class TransitBlindIndex(OpenBaoTransitClient transit) : IBlindIndex
{
    private const string VaultPrefix = "vault:";
    private const string IdxPrefix = "idx:";

    public async Task<string> IndexAsync(PiiClass classification, string value, CancellationToken ct = default)
    {
        var normalized = PiiNormalization.Normalize(classification, value);
        var key = $"tenant-{CipherContext.RequiredTenantId}-idx";
        var hmac = await transit.HmacAsync(key, normalized, ct);
        return hmac.StartsWith(VaultPrefix, StringComparison.Ordinal)
            ? IdxPrefix + hmac[VaultPrefix.Length..]
            : IdxPrefix + hmac;
    }
}
