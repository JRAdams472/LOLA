using Lola.Application.Fees;
using Lola.Domain.Compliance;

namespace Lola.Application.Compliance;

/// Pure baseline-resolution + diff orchestration (M21 P2). Given the
/// version lineage and reset ledger, computes which fee lines form
/// each compare line's statutory baseline, then delegates to
/// `TridTolerance` for classification + thresholds.
///
/// Baseline semantics (Reg Z §1026.19(e)(3)-(4)):
///  * fee covered by a reset stamped to the COMPARE version →
///    self-baseline (delta 0 — the re-issue legitimized the change)
///  * fee covered by a reset stamped to an earlier version V →
///    V's line is the baseline
///  * otherwise → the seq-1 version's line
/// Lines present in a fee's baseline but absent from compare count
/// as removals (negative variance).
public static class ToleranceEvaluator
{
    public sealed record Report(
        IReadOnlyList<TridTolerance.BucketVariance> Variances,
        IReadOnlyList<TridTolerance.Violation> Violations);

    /// `itemsFor` resolves an eval_id to its effective fee lines —
    /// the caller folds overrides in (an override is the disclosed
    /// amount; the snapshot stays immutable).
    public static Report Evaluate(
        DisclosureVersionRecord compare,
        IReadOnlyList<DisclosureVersionRecord> versions,
        IReadOnlyList<ToleranceResetRecord> resets,
        IReadOnlyDictionary<string, ToleranceClassRecord> classes,
        IReadOnlyDictionary<string, IReadOnlyList<FeeItemRecord>> itemsFor,
        IReadOnlyList<FeeOverrideRecord> overrides,
        string evalVariant)
    {
        if (compare.Seq <= 1)
            return new Report([], []);

        var resetsByVersion = resets
            .Where(r => r.RebasedVersionId is not null)
            .GroupBy(r => r.RebasedVersionId!)
            .ToDictionary(g => g.Key, g => g
                .SelectMany(r => CodesIn(r.AffectedFeeCodesJson))
                .ToHashSet(StringComparer.Ordinal));
        bool Covered(string code, DisclosureVersionRecord v) =>
            resetsByVersion.TryGetValue(v.VersionId, out var codes)
            && codes.Contains(code);

        var first = versions.Where(v => v.Seq == 1).FirstOrDefault();
        var compareLines = Lines(compare.EvalId, itemsFor, overrides,
            evalVariant, classes).ToList();

        var baseline = new List<TridTolerance.ClassifiedLine>();
        var compareLookup = compareLines
            .GroupBy(l => l.FeeCode)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));

        // Every fee that ever appeared in a prior version is a
        // baseline candidate — removed fees keep a baseline line.
        var priorFees = versions.Where(v => v.Seq < compare.Seq)
            .SelectMany(v => Lines(v.EvalId, itemsFor, overrides,
                evalVariant, classes))
            .Select(l => l.FeeCode)
            .ToHashSet(StringComparer.Ordinal);
        priorFees.UnionWith(compareLookup.Keys);

        foreach (var code in priorFees)
        {
            if (Covered(code, compare))
            {
                // Reset-rebased at this version → self-baseline.
                baseline.Add(new TridTolerance.ClassifiedLine(code,
                    BucketOf(code, compareLines, classes),
                    compareLookup.GetValueOrDefault(code)));
                continue;
            }
            // Newest stamped baseline for this fee, else seq 1.
            var baseVersion = versions
                .Where(v => v.Seq < compare.Seq && Covered(code, v))
                .OrderByDescending(v => v.Seq)
                .FirstOrDefault() ?? first;
            var amount = baseVersion is null ? 0m
                : Lines(baseVersion.EvalId, itemsFor, overrides,
                    evalVariant, classes)
                    .Where(l => l.FeeCode == code)
                    .Sum(l => l.Amount);
            baseline.Add(new TridTolerance.ClassifiedLine(code,
                BucketOf(code, compareLines, classes, baseVersion,
                    itemsFor, overrides, evalVariant), amount));
        }

        var diff = TridTolerance.Diff(baseline, compareLines);
        return new Report(diff, TridTolerance.Violations(diff));
    }

    /// Effective amount per fee_code — override wins, else the
    /// item's computed total (same aggregation FeeAggregator uses).
    private static IEnumerable<TridTolerance.ClassifiedLine> Lines(
        string evalId,
        IReadOnlyDictionary<string, IReadOnlyList<FeeItemRecord>>
            itemsFor,
        IReadOnlyList<FeeOverrideRecord> overrides,
        string evalVariant,
        IReadOnlyDictionary<string, ToleranceClassRecord> classes)
    {
        if (!itemsFor.TryGetValue(evalId, out var items))
            yield break;
        var live = overrides.Where(o =>
            o.ClearedUtc is null && o.Stale == 0
            && o.Variant == evalVariant).ToList();
        foreach (var item in items)
        {
            var amount = live.FirstOrDefault(o =>
                o.FeeCode == item.FeeCode && o.Section == item.Section)
                is { } o ? (decimal)o.Amount : (decimal)item.Total;
            yield return new TridTolerance.ClassifiedLine(
                item.FeeCode,
                TridTolerance.Classify(item.FeeCode, item.Section,
                    item.PaidTo, item.SelectedFromList is null or > 0,
                    classes.TryGetValue(item.FeeCode, out var cls)
                        ? new TridTolerance.ToleranceClass(
                            cls.Bucket, cls.Shoppable != 0) : null),
                amount);
        }
    }

    private static ToleranceBucket BucketOf(
        string feeCode,
        IReadOnlyList<TridTolerance.ClassifiedLine> compareLines,
        IReadOnlyDictionary<string, ToleranceClassRecord> classes,
        DisclosureVersionRecord? baseVersion = null,
        IReadOnlyDictionary<string, IReadOnlyList<FeeItemRecord>>?
            itemsFor = null,
        IReadOnlyList<FeeOverrideRecord>? overrides = null,
        string? evalVariant = null)
    {
        var inCompare = compareLines
            .FirstOrDefault(l => l.FeeCode == feeCode);
        if (inCompare is not null) return inCompare.Bucket;
        if (baseVersion is not null && itemsFor is not null)
            return Lines(baseVersion.EvalId, itemsFor,
                overrides ?? [], evalVariant ?? "", classes)
                .FirstOrDefault(l => l.FeeCode == feeCode)?.Bucket
                ?? ToleranceBucket.Zero;
        return ToleranceBucket.Zero; // fail closed
    }

    internal static IReadOnlyList<string> CodesIn(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer
                .Deserialize<List<string>>(json) ?? [];
        }
        catch { return []; }
    }
}
