using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;

namespace Lola.Application.Events;

/// Staff condition list — full detail. Borrower access is forbidden
/// here; the borrower projection is ListBorrowerConditionsQuery.
public sealed record ListConditionsQuery(
    string TenantId, string AppId, string ActorId, ActorScope Scope,
    string? State = null);

public static class ListConditionsHandler
{
    public static async Task<IReadOnlyList<ConditionDto>> Handle(
        ListConditionsQuery q, IConditionRepository conditions,
        IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope != ActorScope.Staff)
            throw DomainException.Forbidden(
                "condition detail is staff-only");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, ActorScope.Staff, ct);
        var all = await conditions.ListByApplicationAsync(
            q.AppId, q.State, ct);
        return all.Select(ConditionDto.Of).ToList();
    }
}

/// Borrower/partner-scoped condition view (M12 projection; partner
/// admission MPE-266) — the public-field projection is identical for
/// both external scopes; only the reach check differs (own app vs
/// active link). Staff actors get an empty list: this surface is
/// external-scope by construction.
public sealed record ListBorrowerConditionsQuery(
    string TenantId, string AppId, string ActorId,
    ActorScope Scope = ActorScope.Borrower);

public static class ListBorrowerConditionsHandler
{
    public static async Task<IReadOnlyList<BorrowerConditionDto>> Handle(
        ListBorrowerConditionsQuery q, IConditionRepository conditions,
        IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope == ActorScope.Staff)
            throw DomainException.Forbidden(
                "staff conditions use the full projection");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.Scope, ct);
        var all = await conditions.ListByApplicationAsync(q.AppId,
            state: null, ct);
        return all.Select(BorrowerConditionDto.Of).ToList();
    }
}
