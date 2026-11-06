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
    public static async Task<FeeComparisonDto?> Handle(
        GetApplicationFeesQuery q, IApplicationRepository apps,
        IFeeRepository fees, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.ActorIsBorrower, ct);
        var batch = await fees.GetLatestBatchEvalsAsync(q.AppId, ct);
        if (batch.Count == 0) return null;
        var overrides = await fees.ListOverridesAsync(q.AppId, ct);
        var evals = new List<FeeBreakdownDto>(batch.Count);
        foreach (var e in batch)
        {
            var items = await fees.ListItemsAsync(e.EvalId, ct);
            evals.Add(FeeAggregator.ToBreakdown(e, items, overrides));
        }
        return new FeeComparisonDto(
            batch[0].BatchId ?? batch[0].EvalId, evals);
    }
}
