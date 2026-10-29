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

        return await eraser.EraseAsync(tenant, ct);
    }
}
