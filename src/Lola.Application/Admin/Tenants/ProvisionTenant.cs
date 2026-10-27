using Lola.Application.Identity;

namespace Lola.Application.Admin.Tenants;

public sealed record ProvisionTenantCommand(string TenantId, string Slug, string DisplayName);

/// Provisioning result — `ServiceClientSecret` is the svc-client
/// plaintext exactly once (first creation); re-runs return null. It is
/// deliberately NOT [PiiClass]-marked so the masked invoke passes it
/// through to the TenantAdmin/Service caller that must record it.
public sealed record ProvisionTenantResult(
    TenantAdminDto Tenant, IReadOnlyList<string> Steps,
    string ServiceClientId, string? ServiceClientSecret);

/// Tenant creation path — deliberately NOT RequireTenant-guarded: the
/// tenant does not exist yet when this runs. Edge policy restricts it
/// to TenantAdmin/Service (MPE-87).
public static class ProvisionTenantHandler
{
    public static async Task<ProvisionTenantResult> Handle(
        ProvisionTenantCommand c, ITenantProvisioner provisioner, CancellationToken ct)
    {
        var result = await provisioner.ProvisionAsync(
            new ProvisionRequest(c.TenantId, c.Slug, c.DisplayName), ct);
        var t = result.Tenant;
        return new ProvisionTenantResult(
            new TenantAdminDto(t.Id, t.Slug, t.DisplayName, t.Status, t.CreatedUtc),
            result.StepsCompleted,
            result.ServiceClientId ?? $"svc-{t.Id}",
            result.ServiceClientSecret);
    }
}
