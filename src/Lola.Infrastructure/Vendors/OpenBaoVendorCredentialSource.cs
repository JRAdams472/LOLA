using Lola.Application.Tenancy;
using Lola.Application.Vendors;
using Lola.Infrastructure.OpenBao;

namespace Lola.Infrastructure.Vendors;

/// Resolves the CURRENT tenant's vendor credentials from OpenBao KV
/// (MPE-226): `lola-kv/vendors/{tenantId}/{kind}` →
/// `{api_key, webhook_secret}`. The ONLY credential path for vendor
/// adapters — nothing reads creds from config, registry rows, or
/// the repo (ticket invariant). Returns null on a miss; the adapter
/// turns that into a typed auth failure rather than falling back to
/// a shared credential.
public sealed class OpenBaoVendorCredentialSource(OpenBaoKvClient kv)
    : IVendorCredentialSource
{
    public static string Path(string tenantId, VendorKind kind)
        => $"vendors/{tenantId}/{Token(kind)}";

    public async Task<VendorCredentials?> ForAsync(VendorKind kind,
        CancellationToken ct = default)
    {
        if (!kv.Configured) return null;
        var tenantId = TenantContext.Required.TenantId;
        var data = await kv.ReadAsync(Path(tenantId, kind), ct);
        if (data is null) return null;
        data.TryGetValue("api_key", out var apiKey);
        data.TryGetValue("webhook_secret", out var webhookSecret);
        return new VendorCredentials(apiKey, webhookSecret);
    }

    private static string Token(VendorKind kind) => kind switch
    {
        VendorKind.Credit => "credit",
        VendorKind.Voe => "voe",
        VendorKind.Voi => "voi",
        VendorKind.Flood => "flood",
        VendorKind.Fraud => "fraud",
        VendorKind.Aus => "aus",
        _ => "unknown"
    };
}
