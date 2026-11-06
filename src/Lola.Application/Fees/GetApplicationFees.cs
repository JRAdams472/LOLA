using Lola.Application.Admin;
using Lola.Application.Applications;

namespace Lola.Application.Fees;

/// GET …/applications/{id}/fees (MPE-183) — the latest eval for the
/// application, whatever its status (pending/done/failed ride in the
/// DTO). Null when the application has never been fee-evaluated.
public sealed record GetApplicationFeesQuery(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower);

public static class GetApplicationFeesHandler
{
    public static async Task<FeeBreakdownDto?> Handle(
        GetApplicationFeesQuery q, IApplicationRepository apps,
        IFeeRepository fees, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.ActorIsBorrower, ct);
        var latest = await fees.GetLatestEvalAsync(q.AppId, ct);
        if (latest is null) return null;
        var items = await fees.ListItemsAsync(latest.EvalId, ct);
        var overrides = await fees.ListOverridesAsync(q.AppId, ct);
        return FeeAggregator.ToBreakdown(latest, items, overrides);
    }
}
