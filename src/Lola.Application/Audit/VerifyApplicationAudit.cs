using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;

namespace Lola.Application.Audit;

/// Chain verification for the tenant's audit trail — staff-only
/// (audit integrity is a compliance surface, not a borrower feature).
/// Returns whether every event recomputes and links, plus the first
/// broken seq on failure.
public sealed record VerifyAuditQuery(
    string TenantId, string ActorId, ActorScope Scope);

public static class VerifyAuditHandler
{
    public static async Task<AuditVerifyResult> Handle(
        VerifyAuditQuery q, ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope != ActorScope.Staff)
            throw DomainException.Forbidden("audit verification is staff-only");
        return await audit.VerifyChainAsync(ct);
    }
}
