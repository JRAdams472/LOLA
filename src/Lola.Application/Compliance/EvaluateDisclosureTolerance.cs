using Lola.Application.Events;
using Lola.Application.Fees;

namespace Lola.Application.Compliance;

/// Runs the TRID tolerance diff when a le/cd package completes
/// (M21 P2, MPE-298). Called from the doc outbox drain after
/// `CompleteAsync` — the version row was bound at enqueue. Violations
/// write a hash-chained `loan_event` and open ONE blocking
/// `tolerance_cure` condition per application (deduped while open).
public static class EvaluateDisclosureTolerance
{
    public static async Task Handle(
        string appId, string docId, string actor,
        IToleranceRepository tolerance, IFeeRepository fees,
        IConditionRepository conditions, ILoanEventLog events,
        CancellationToken ct)
    {
        var version = await tolerance.GetVersionByDocAsync(docId, ct);
        if (version is null || version.Seq <= 1)
            return; // no binding, or the baseline itself

        var versions = await tolerance.ListVersionsAsync(
            appId, version.Kind, ct);
        var resets = await tolerance.ListResetsAsync(appId, ct);
        var classes = await tolerance.GetClassesAsync(ct);

        var evals = versions.Select(v => v.EvalId).Distinct().ToList();
        var items = new Dictionary<string,
            IReadOnlyList<FeeItemRecord>>();
        string variant = "";
        foreach (var evalId in evals)
        {
            items[evalId] = await fees.ListItemsAsync(evalId, ct);
            if (evalId == version.EvalId)
            {
                var eval = await fees.GetEvalAsync(evalId, ct);
                variant = eval?.Variant ?? "";
            }
        }
        var overrides = await fees.ListOverridesAsync(appId, ct);

        var report = ToleranceEvaluator.Evaluate(version, versions,
            resets, classes, items, overrides, variant);
        if (report.Violations.Count == 0)
            return;

        var summary = string.Join("; ", report.Violations.Select(v =>
            $"{v.Rule} {v.FeeCode ?? "aggregate"} " +
            $"{v.Baseline:0.00}→{v.Compare:0.00}"));
        await events.WriteAsync(new LoanEventDraft(appId,
            LoanEventKind.ToleranceViolation, actor,
            "disclosure_version", version.VersionId,
            "tolerance", null, null,
            $"{report.Violations.Count} violation(s): {summary}"), ct);

        var open = (await conditions.ListByApplicationAsync(
                appId, ConditionState.Open, ct))
            .FirstOrDefault(c => c.Kind == ConditionKind.ToleranceCure);
        if (open is null)
            await conditions.OpenAsync(new ConditionDraft(appId,
                ConditionKind.ToleranceCure,
                "TRID tolerance violation — cure required",
                summary, Blocking: true,
                LinkedEntity: ConditionLink.DisclosureVersion,
                LinkedEntityId: version.VersionId), ct);
    }
}
