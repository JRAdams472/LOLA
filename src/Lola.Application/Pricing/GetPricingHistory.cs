using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;

namespace Lola.Application.Pricing;

/// GET …/applications/{id}/pricing/history (MPE-130) — every eval for
/// the application oldest-first; the append-only `pricing_evals`
/// table is the spine behind this endpoint.
public sealed record GetPricingHistoryQuery(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class GetPricingHistoryHandler
{
    public static async Task<IReadOnlyList<PricingEvalDto>> Handle(
        GetPricingHistoryQuery q, IApplicationRepository apps,
        IPricingRepository pricing, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope != ActorScope.Staff)
            throw DomainException.Forbidden("pricing history is staff-only");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.Scope, ct);
        var rows = await pricing.ListEvalsAsync(q.AppId, ct);
        return rows.Select(PricingWire.ToDto).ToList();
    }
}
