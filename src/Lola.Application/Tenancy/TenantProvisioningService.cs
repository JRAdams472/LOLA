using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Identity;
using Lola.Domain.Tenancy;

namespace Lola.Application.Tenancy;

/// Tenant provisioning job — the ordered, re-entrant pipeline:
///   RegistryStep  → registry row (status=provisioning)
///   KeysStep      → transit DEK + index key (idempotent ensure)
///   DataFileStep  → SQLCipher file + wrapped file key on the row
///   ActivateStep  → status=active
/// A row left in `provisioning` IS the failure state — there is no
/// 'failed' status because every step is safe to re-run: the next
/// ProvisionAsync call resumes where the last one stopped. The
/// downstream client-credentials step (Phase 4) appends here without
/// changing this contract.
public sealed class TenantProvisioningService(
    ITenantRegistry registry,
    ITenantKeyManager keys,
    ITenantDbProvisioner dataPlane,
    string tenantDataDir = "data/tenants") : ITenantProvisioner
{
    public async Task<ProvisionResult> ProvisionAsync(
        ProvisionRequest request, CancellationToken ct = default)
    {
        var tenantId = TenantId.Validate(request.TenantId);
        if (!TenantId.IsValid(request.Slug))
            throw new ArgumentException("Slug must be a valid tenant-id shape.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            throw new ArgumentException("Display name is required.", nameof(request));

        var steps = new List<string>();
        var tenant = await registry.FindByIdAsync(tenantId, ct);
        if (tenant is null)
        {
            var dbPath = Path.Combine(tenantDataDir, $"{tenantId}.db");
            await registry.InsertProvisioningAsync(tenantId, request.Slug, request.DisplayName, dbPath, ct);
            tenant = await registry.FindByIdAsync(tenantId, ct)
                ?? throw new InvalidOperationException("Tenant registry insert did not persist.");
            steps.Add("registry");
        }

        // Transit keys before the file: the file key is wrapped by them.
        await keys.EnsureKeysAsync(tenantId, ct);
        steps.Add("transit-keys");

        if (tenant.DbKeyWrapped is null)
        {
            var wrapped = await dataPlane.ProvisionFileAsync(tenant, ct);
            await registry.SetDbKeyAsync(tenantId, wrapped, ct);
            steps.Add("data-file");
        }

        if (tenant.Status != "active")
        {
            await registry.SetStatusAsync(tenantId, "active", ct);
            steps.Add("activate");
        }

        tenant = await registry.FindByIdAsync(tenantId, ct) ?? tenant;
        return new ProvisionResult(tenant, steps);
    }
}
