using Lola.Application.Events;
using Lola.Application.Vendors;

namespace Lola.Application.Aus;

/// Shared hook invoked at every vendor-order completion site
/// (in-band opportunistic, poll drain, webhook) — when the completed
/// order is kind=aus with a successful outcome, the normalized report
/// flows into the append-only AUS store + condition auto-population.
/// One small helper rather than three copies of the branching.
public static class AusCompletionHook
{
    public static async Task TryProcessAsync(VendorKind kind,
        string appId, string orderId, VendorResult result,
        string actor, IAusResultsStore store,
        IConditionRepository conditions, CancellationToken ct)
    {
        if (kind != VendorKind.Aus || result.Outcome != "completed")
            return;
        await ProcessAusCompletion.Handle(appId, orderId,
            "unknown", result.Report, actor, store, conditions, ct);
    }
}
