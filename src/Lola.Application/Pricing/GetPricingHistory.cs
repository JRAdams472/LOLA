using Lola.Application.Admin;
using Lola.Application.Applications;

namespace Lola.Application.Pricing;

/// GET …/applications/{id}/pricing/history (MPE-130) — every eval for
/// the application oldest-first; the append-only `pricing_evals`
/// table is the spine behind this endpoint.
public sealed record GetPricingHistoryQuery(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower);

public static class GetPricingHistoryHandler
{
    public static async Task<IReadOnlyList<PricingEvalDto>> Handle(
        GetPricingHistoryQuery q, IApplicationRepository apps,
        IPricingRepository pricing, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.ActorIsBorrower, ct);
        var rows = await pricing.ListEvalsAsync(q.AppId, ct);
        return rows.Select(PricingWire.ToDto).ToList();
    }
}
