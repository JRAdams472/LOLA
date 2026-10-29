using Lola.Application.Admin;
using Lola.Application.Errors;

namespace Lola.Application.Audit;

/// Per-application access-review report (MPE-117) — who touched this
/// application, what action, which PII classes, when. Staff-only; the
/// report exposes attribution metadata, never field values.
public sealed record GetApplicationAuditQuery(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower);

public static class GetApplicationAuditHandler
{
    public static async Task<IReadOnlyList<AuditEvent>> Handle(
        GetApplicationAuditQuery q, ITenantAuditLog audit,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden("audit reports are staff-only");
        return await audit.QueryAsync(
            new AuditFilter(Entity: "application", EntityId: q.AppId), ct);
    }
}
