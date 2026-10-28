using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Identity;
using Lola.Domain.Tenancy;

namespace Lola.Application.Tenancy;

/// Tenant provisioning job — the ordered, re-entrant pipeline:
///   RegistryStep  → registry row (status=provisioning)
///   KeysStep      → transit DEK + index key (idempotent ensure)
///   DataFileStep  → SQLCipher file + wrapped file key on the row
///   MigrateStep   → tenant-file schema to latest (MPE-93)
///   ActivateStep  → status=active
///   SvcClientStep → `svc-{tenant}` client-credentials OIDC client
/// A row left in `provisioning` IS the failure state — there is no
/// 'failed' status because every step is safe to re-run: the next
/// ProvisionAsync call resumes where the last one stopped.
public sealed class TenantProvisioningService(
    ITenantRegistry registry,
    ITenantKeyManager keys,
    ITenantDbProvisioner dataPlane,
    IServiceClientProvisioner svcClients,
    ITenantMigrationRunner migrations,
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
            tenant = await registry.FindByIdAsync(tenantId, ct) ?? tenant;
            steps.Add("data-file");
        }

        // Schema to latest before activation — a failed migrate leaves
        // the row in `provisioning` and the next run resumes here.
        var migration = await migrations.MigrateAsync(tenant, ct);
        if (!migration.Success)
            throw new InvalidOperationException(
                $"Tenant migration failed: {migration.Error}");
        steps.Add("migrate");

        if (tenant.Status != "active")
        {
            await registry.SetStatusAsync(tenantId, "active", ct);
            steps.Add("activate");
        }

        // Downstream service credentials — idempotent; the secret is
        // plaintext exactly once, on first creation.
        var svc = await svcClients.EnsureAsync(tenantId, ct);
        steps.Add("svc-client");

        tenant = await registry.FindByIdAsync(tenantId, ct) ?? tenant;
        return new ProvisionResult(tenant, steps, svc.ClientId, svc.PlaintextSecret);
    }
}
