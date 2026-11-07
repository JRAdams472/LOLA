using Lola.Application.Audit;
using Lola.Application.Documents;
using Lola.Application.Errors;
using Lola.Application.Identity;

namespace Lola.Application.Admin.Tenants;

/// POST /api/admin/tenants/{id}/isppr/republish-templates (MPE-191) —
/// ops action: re-uploads the four manifest templates to the tenant's
/// ISPPr client as NEW versions (publish-version for existing masters,
/// create for any that were never provisioned). No re-provisioning —
/// client, customer, and key are untouched. Repeat calls just publish
/// another version, so the action is safe to retry.
public sealed record RepublishIspprTemplatesCommand(
    string TenantId, string ActorId);

public sealed record RepublishedTemplate(string Name, long Version);

public sealed record RepublishResult(
    IReadOnlyList<RepublishedTemplate> Templates);

public static class RepublishIspprTemplatesHandler
{
    public static async Task<RepublishResult> Handle(
        RepublishIspprTemplatesCommand c, ITenantRegistry registry,
        IDocVendorAdmin admin, IControlPlaneAuditLog audit,
        CancellationToken ct)
    {
        var tenant = await registry.FindByIdAsync(c.TenantId, ct)
            ?? throw DomainException.NotFound("tenant not found");
        var clientId = tenant.IspprClientId
            ?? throw DomainException.Conflict(
                "tenant has no document-vendor binding");

        var templates = TemplateManifest.AllTemplates()
            .ToDictionary(n => n, TemplateManifest.SourceFor);
        var versions = await admin.RepublishTemplatesAsync(
            clientId, templates, ct);

        // Control-plane log — the operator action outlives any
        // tenant-file shredding, same as erasure attribution.
        await audit.WriteAsync(c.ActorId, "isppr_templates_republished",
            "tenant", c.TenantId, [], null, ct);
        return new RepublishResult(versions
            .Select(kv => new RepublishedTemplate(kv.Key, kv.Value))
            .ToList());
    }
}
