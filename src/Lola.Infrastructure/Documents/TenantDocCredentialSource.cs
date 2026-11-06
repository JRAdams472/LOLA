using Lola.Application.Documents;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;

namespace Lola.Infrastructure.Documents;

/// Resolves the CURRENT tenant's ISPPr API key for one outbound call
/// (MPE-190): registry row → transit-decrypt under the ambient
/// CipherContext (set by tenant middleware on requests and by the
/// outbox drain per tenant). Returns null when the tenant has no
/// vendor binding yet — the adapter turns that into a typed Auth
/// failure rather than leaking a shared credential.
public sealed class TenantDocCredentialSource(
    ITenantRegistry registry, IFieldCipher cipher)
    : IDocumentCredentialSource
{
    public async Task<string?> ApiKeyAsync(
        CancellationToken ct = default)
    {
        var tenantId = TenantContext.Required.TenantId;
        var tenant = await registry.FindByIdAsync(tenantId, ct);
        return tenant?.IspprKeyWrapped is { } wrapped
            ? await cipher.DecryptAsync(wrapped, ct)
            : null;
    }
}
