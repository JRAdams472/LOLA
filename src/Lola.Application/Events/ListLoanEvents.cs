using Lola.Application.Admin;
using Lola.Application.Errors;

namespace Lola.Application.Events;

/// Per-application mutation event list (M15) — the field_change /
/// override / transition / reveal / condition_* spine. Staff-only;
/// the rows carry blind-index tokens for PII classes, never values.
public sealed record GetLoanEventsQuery(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower,
    int Limit = 200);

public static class GetLoanEventsHandler
{
    public static async Task<IReadOnlyList<LoanEvent>> Handle(
        GetLoanEventsQuery q, ILoanEventLog events,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden(
                "mutation events are staff-only");
        return await events.QueryAsync(q.AppId, q.Limit, ct);
    }
}

/// Tenant-level chain verification (M15) — mirrors audit/verify:
/// clean or the first seq that fails recompute/link. Staff-only.
public sealed record VerifyEventChainQuery(
    string TenantId, string ActorId, bool ActorIsBorrower);

public static class VerifyEventChainHandler
{
    public static async Task<EventVerifyResult> Handle(
        VerifyEventChainQuery q, ILoanEventLog events,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden(
                "chain verification is staff-only");
        return await events.VerifyChainAsync(ct);
    }
}
