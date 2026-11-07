using Lola.Application.Documents;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Identity;

namespace Lola.Infrastructure.Documents;

/// Per-tenant ISPPr provisioning step (MPE-190): wires the vendor
/// binding onto the registry row —
///   client (name=slug) → customer (external_ref=slug)
///   → api-key (generate scope, wrapped under the tenant transit key)
///   → the four manifest-declared template masters (409 = skip).
/// Re-entrant like every provisioning step: ids that already exist on
/// the row are reused, and the plaintext key is created only when no
/// wrapped key is stored — plaintext crosses exactly once, then only
/// `vault:` ciphertext + the key id persist.
public sealed class IspprTenantProvisioner(
    ITenantRegistry registry, IDocVendorAdmin admin,
    IFieldCipher cipher) : ITenantDocProvisioner
{
    public async Task EnsureAsync(TenantRecord tenant,
        CancellationToken ct = default)
    {
        var clientId = tenant.IspprClientId
            ?? await admin.EnsureClientAsync(tenant.Slug, ct);
        var customerId = tenant.IspprCustomerId
            ?? await admin.EnsureCustomerAsync(
                clientId, tenant.Slug, tenant.Slug, ct);
        if (tenant.IspprClientId is null || tenant.IspprCustomerId is null)
            await registry.SetIspprIdsAsync(
                tenant.Id, clientId, customerId, ct);

        if (tenant.IspprKeyWrapped is null)
        {
            var (keyId, plaintext) = await admin.CreateApiKeyAsync(
                clientId, $"lola-{tenant.Slug}", ["generate"], ct);
            // Wrap under the tenant transit key — the plaintext never
            // touches the registry or a log line.
            string wrapped;
            using (CipherContext.Use(tenant.Id))
                wrapped = await cipher.EncryptAsync(plaintext, ct);
            await registry.SetIspprKeyAsync(
                tenant.Id, keyId, wrapped, ct);
        }

        foreach (var name in TemplateManifest.AllTemplates())
            await admin.UploadTemplateAsync(
                clientId, name, TemplateManifest.SourceFor(name), ct);
    }
}
