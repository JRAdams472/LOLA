using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Application.Fees;
using Lola.Application.Identity;

namespace Lola.Application.Compliance;

/// GET …/applications/{id}/tolerance (M21 P2) — version lineage,
/// resets, and the latest version's per-bucket variance + violations
/// recomputed against the stamped baseline (read-only — pending
/// resets don't shift the report until their re-issue lands).
public sealed record GetApplicationToleranceQuery(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class GetApplicationToleranceHandler
{
    public static async Task<ToleranceReportDto> Handle(
        GetApplicationToleranceQuery q, IApplicationRepository apps,
        IToleranceRepository tolerance, IFeeRepository fees,
        IConditionRepository conditions, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "tolerance posture is staff-facing");
        await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, ActorScope.Staff, ct);

        var resets = await tolerance.ListResetsAsync(q.AppId, ct);
        var classes = await tolerance.GetClassesAsync(ct);
        var overrides = await fees.ListOverridesAsync(q.AppId, ct);
        var open = (await conditions.ListByApplicationAsync(
                q.AppId, ConditionState.Open, ct))
            .FirstOrDefault(k => k.Kind == ConditionKind.ToleranceCure);

        var all = new List<DisclosureVersionDto>();
        var buckets = new List<BucketVarianceDto>();
        var violations = new List<ViolationDto>();

        foreach (var kind in new[] { "le", "cd" })
        {
            var versions = await tolerance.ListVersionsAsync(
                q.AppId, kind, ct);
            all.AddRange(versions.Select(v =>
                new DisclosureVersionDto(v.VersionId, v.Kind, v.EvalId,
                    v.DocId, v.Seq, v.ResetOfVersionId, v.CreatedUtc)));
            var latest = versions.OrderByDescending(v => v.Seq)
                .FirstOrDefault();
            if (latest is null || latest.Seq <= 1) continue;

            var items = new Dictionary<string,
                IReadOnlyList<FeeItemRecord>>();
            foreach (var evalId in
                versions.Select(v => v.EvalId).Distinct())
                items[evalId] = await fees.ListItemsAsync(evalId, ct);
            var eval = await fees.GetEvalAsync(latest.EvalId, ct);

            var report = ToleranceEvaluator.Evaluate(latest, versions,
                resets, classes, items, overrides,
                eval?.Variant ?? "");
            buckets.AddRange(report.Variances.Select(b =>
                new BucketVarianceDto(b.Bucket.ToString(), b.Baseline,
                    b.Compare, b.Lines.Select(l => new LineVarianceDto(
                        l.FeeCode, l.Baseline, l.Compare, l.Delta))
                    .ToList())));
            violations.AddRange(report.Violations.Select(v =>
                new ViolationDto(v.Bucket.ToString(), v.FeeCode,
                    v.Baseline, v.Compare, v.Allowed, v.Rule)));
        }

        return new ToleranceReportDto(all,
            resets.Select(r => new ToleranceResetDto(r.ResetId,
                r.ReasonCode,
                ToleranceEvaluator.CodesIn(r.AffectedFeeCodesJson),
                r.RebasedVersionId, r.Actor, r.CreatedUtc)).ToList(),
            buckets, violations, open?.ConditionId);
    }
}
