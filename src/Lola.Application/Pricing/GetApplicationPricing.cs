using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;

namespace Lola.Application.Pricing;

/// GET …/applications/{id}/pricing (MPE-130) — the latest eval for
/// the application, whatever its status (pending/done/failed ride in
/// the DTO). Null when the application has never been priced.
public sealed record GetApplicationPricingQuery(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower);

public static class GetApplicationPricingHandler
{
    public static async Task<PricingEvalDto?> Handle(
        GetApplicationPricingQuery q, IApplicationRepository apps,
        IPricingRepository pricing, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.ActorIsBorrower, ct);
        var latest = await pricing.GetLatestEvalAsync(q.AppId, ct);
        return latest is null ? null : PricingWire.ToDto(latest);
    }
}
