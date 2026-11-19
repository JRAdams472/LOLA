using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;

namespace Lola.Application.Events;

/// Staff condition list — full detail. Borrower access is forbidden
/// here; the borrower projection is ListBorrowerConditionsQuery.
public sealed record ListConditionsQuery(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower,
    string? State = null);

public static class ListConditionsHandler
{
    public static async Task<IReadOnlyList<ConditionDto>> Handle(
        ListConditionsQuery q, IConditionRepository conditions,
        IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden(
                "condition detail is staff-only");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, isBorrower: false, ct);
        var all = await conditions.ListByApplicationAsync(
            q.AppId, q.State, ct);
        return all.Select(ConditionDto.Of).ToList();
    }
}

/// Borrower-scoped condition view (M12 projection) — own application
/// only, public fields. Staff actors get an empty list: this surface
/// is borrower-scope by construction (mirrors GetPortalLoanQuery).
public sealed record ListBorrowerConditionsQuery(
    string TenantId, string AppId, string ActorId);

public static class ListBorrowerConditionsHandler
{
    public static async Task<IReadOnlyList<BorrowerConditionDto>> Handle(
        ListBorrowerConditionsQuery q, IConditionRepository conditions,
        IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, isBorrower: true, ct);
        var all = await conditions.ListByApplicationAsync(q.AppId,
            state: null, ct);
        return all.Select(BorrowerConditionDto.Of).ToList();
    }
}
