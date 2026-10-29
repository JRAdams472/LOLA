using Lola.Application.Admin;
using Lola.Application.Errors;

namespace Lola.Application.Audit;

/// Tenant-wide access-review report (MPE-117) — every audit event in
/// the tenant file, filterable by actor / action / created_utc window.
/// Staff-only; attribution metadata only, never values.
public sealed record GetTenantAuditQuery(
    string TenantId, string ActorId, bool ActorIsBorrower,
    string? FilterActor = null, string? FilterAction = null,
    string? Since = null, string? Until = null);

public static class GetTenantAuditHandler
{
    public static async Task<IReadOnlyList<AuditEvent>> Handle(
        GetTenantAuditQuery q, ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden("audit reports are staff-only");
        return await audit.QueryAsync(new AuditFilter(
            Actor: q.FilterActor, Action: q.FilterAction,
            Since: q.Since, Until: q.Until), ct);
    }
}
