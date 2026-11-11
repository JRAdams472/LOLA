using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Domain.Applications;

namespace Lola.Application.Admin.Tenants;

public sealed record EraseTenantCommand(
    string TenantId, bool Force, string? Reason, string ActorId);

/// Destructive tenant erasure (MPE-96): the handler guards what the
/// endpoint's Provision policy can't — the tenant must exist and must
/// not already be retired.
///
/// Reg B retention gate (MPE-118): erasure crypto-shreds every
/// application record, so it is refused while any application sits
/// inside its 25-month window — the refusal carries the earliest-clear
/// date. `force` + a non-empty `reason` overrides the window and writes
/// a `retention_override` event to the CONTROL-PLANE audit log (the
/// in-file log is shredded with the data it describes).
public static class EraseTenantHandler
{
    public static async Task<TenantErasureResult> Handle(
        EraseTenantCommand c, ITenantRegistry registry,
        IApplicationRepository apps, ITenantEraser eraser,
        IControlPlaneAuditLog audit, TimeProvider clock,
        Documents.IDocVendorAdmin docAdmin,
        IEnumerable<Vault.ITenantVaultProvisioner> vaultProvisioners,
        CancellationToken ct)
    {
        var tenant = await registry.FindByIdAsync(c.TenantId, ct)
            ?? throw DomainException.NotFound("tenant not found");
        if (tenant.Status == "retired")
            throw DomainException.BadRequest("tenant is already retired");

        // Read application windows from the tenant file — scope comes
        // from the registry record, so tid-less operator principals
        // resolve the same file the eraser would shred.
        DateTimeOffset? clearAt;
        using (TenantContext.Use(TenantScope.From(tenant)))
        {
            var windows = await apps.RetentionWindowsAsync(ct);
            clearAt = windows.Count == 0 ? null
                : windows.Max(w => RetentionPolicy.Until(w.CreatedUtc));
        }

        if (clearAt is not null && clock.GetUtcNow() < clearAt.Value)
        {
            if (!c.Force)
                throw new DomainException(409,
                    $"tenant is inside the Reg B retention window until " +
                    $"{clearAt.Value:O} — retry with force and a reason");
            if (string.IsNullOrWhiteSpace(c.Reason))
                throw DomainException.BadRequest(
                    "a reason is required for a retention override");
            await audit.WriteAsync(c.ActorId, "retention_override",
                "tenant", c.TenantId, [], c.Reason, ct);
        }

        // Vendor credential dies BEFORE the tenant keys (MPE-191):
        // the wrapped material crypto-shreds either way, but the
        // vendor still holds a working copy of the key. Revoking
        // first keeps the destructive order fail-closed — a failed
        // revoke aborts the whole erasure with nothing yet destroyed,
        // so the operation stays retryable. Revoke is idempotent
        // vendor-side, so a retried erasure converges.
        if (tenant.IspprKeyId is not null)
        {
            await docAdmin.RevokeApiKeyAsync(tenant.IspprKeyId, ct);
            await audit.WriteAsync(c.ActorId, "isppr_key_revoked",
                "tenant", c.TenantId, [], null, ct);
        }

        // Vault bucket purge BEFORE the shred (MPE-202): the tenant's
        // ciphertext objects must not outlive the registry's authority
        // over them. Same fail-closed discipline as the key revoke —
        // a failed purge aborts the whole erasure with nothing yet
        // destroyed (the revoke above is idempotent, so retry
        // converges). Empty under VAULT_ADAPTER=stub: the stub vault
        // is process memory, nothing durable to purge.
        var vaultProvisioner = vaultProvisioners.FirstOrDefault();
        if (vaultProvisioner is not null)
        {
            await vaultProvisioner.PurgeAsync(tenant, ct);
            await audit.WriteAsync(c.ActorId, "vault_purged",
                "tenant", c.TenantId, [], null, ct);
        }

        var result = await eraser.EraseAsync(tenant, ct);
        // The shred destroys the tenant file's own audit log, so the
        // erasure itself is attributed in the CONTROL-PLANE log — the
        // spine (tenants row + tenant_migrations + audit_events) is
        // what survives erasure by design (MPE-119).
        await audit.WriteAsync(c.ActorId, "tenant_erased",
            "tenant", c.TenantId, [], null, ct);
        return result;
    }
}
