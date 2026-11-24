namespace Lola.Domain.Compliance;

/// TRID fee-variance engine — Reg Z §1026.19(e)(3). Every fee on an
/// LE/CD line lands in exactly one bucket:
///
///   Zero      — cannot increase at all: lender/affiliate charges,
///               can't-shop third-party services, transfer taxes.
///   TenPercent— aggregate increases capped at 10%: recording fees +
///               shoppable services picked from the written list.
///   Unlimited — no cap: shopped-off-list services, prepaid interest,
///               escrows.
///
/// Classification fails CLOSED — an unregistered fee_code is 0%, the
/// strictest bucket. The registry (`tolerance_classes`) supplies the
/// bucket hint + shoppable flag; the domain rules below apply
/// §1026.19(e)(3)(i)-(iii) on top.
public static class TridTolerance
{
    /// Paid-to values that force the 0% bucket regardless of registry.
    public static readonly IReadOnlySet<string> LenderPaid =
        new HashSet<string> { "lender", "affiliate", "broker" };

    /// Registry row as the domain sees it — resolved by the caller
    /// from `tolerance_classes`; null = unregistered fee code.
    public sealed record ToleranceClass(string Bucket, bool Shoppable);

    /// One fee line as classified — the diff input unit.
    public sealed record ClassifiedLine(
        string FeeCode, ToleranceBucket Bucket, decimal Amount);

    /// Per-bucket variance between two disclosure versions.
    public sealed record BucketVariance(
        ToleranceBucket Bucket, decimal Baseline, decimal Compare,
        IReadOnlyList<LineVariance> Lines);

    public sealed record LineVariance(
        string FeeCode, decimal Baseline, decimal Compare, decimal Delta);

    /// A threshold breach — 0% any-increase (per line) or 10%
    /// aggregate breach (bucket total).
    public sealed record Violation(
        ToleranceBucket Bucket, string? FeeCode, decimal Baseline,
        decimal Compare, decimal Allowed, string Rule);

    /// §1026.19(e)(3) classification. `paidTo`/`selectedFromList`
    /// come from the fee line; `cls` is the registry row (null →
    /// fail closed to Zero).
    public static ToleranceBucket Classify(
        string feeCode, string section, string? paidTo,
        bool selectedFromList, ToleranceClass? cls)
    {
        if (paidTo is not null && LenderPaid.Contains(paidTo))
            return ToleranceBucket.Zero;
        if (cls is null) return ToleranceBucket.Zero;
        return cls.Bucket switch
        {
            "zero" => ToleranceBucket.Zero,
            // A shoppable fee only earns 10% treatment when the
            // borrower picked the provider off the written list —
            // off-list is unlimited (§1026.19(e)(3)(iii)).
            "ten_percent" => !cls.Shoppable || selectedFromList
                ? ToleranceBucket.TenPercent : ToleranceBucket.Unlimited,
            _ => ToleranceBucket.Unlimited,
        };
    }

    /// Diff two disclosure versions. `baseline` is the last good
    /// estimate set; `compare` is the new version's lines. Returns
    /// per-bucket variances; violations are computed by the caller
    /// via <see cref="Violations"/> so reset-scoped diffs can drop
    /// unaffected fees before calling it.
    public static IReadOnlyList<BucketVariance> Diff(
        IReadOnlyList<ClassifiedLine> baseline,
        IReadOnlyList<ClassifiedLine> compare)
    {
        var byBucket = new Dictionary<ToleranceBucket, List<LineVariance>>();
        var codes = baseline.Select(l => l.FeeCode)
            .Union(compare.Select(l => l.FeeCode));
        foreach (var code in codes)
        {
            var b = baseline.Where(l => l.FeeCode == code).Sum(l => l.Amount);
            var c = compare.Where(l => l.FeeCode == code).Sum(l => l.Amount);
            var bucket = compare.FirstOrDefault(l => l.FeeCode == code)?.Bucket
                ?? baseline.First(l => l.FeeCode == code).Bucket;
            if (!byBucket.TryGetValue(bucket, out var lines))
                byBucket[bucket] = lines = [];
            lines.Add(new LineVariance(code, b, c, c - b));
        }
        return byBucket.Select(kv => new BucketVariance(
            kv.Key,
            kv.Value.Sum(l => l.Baseline),
            kv.Value.Sum(l => l.Compare),
            kv.Value)).ToList();
    }

    /// Apply the bucket thresholds to a diff.
    public static IReadOnlyList<Violation> Violations(
        IReadOnlyList<BucketVariance> diff)
    {
        var result = new List<Violation>();
        foreach (var bucket in diff)
        {
            switch (bucket.Bucket)
            {
                case ToleranceBucket.Zero:
                    // Any increase on any 0% line is a violation.
                    foreach (var line in bucket.Lines.Where(l => l.Delta > 0))
                        result.Add(new Violation(ToleranceBucket.Zero,
                            line.FeeCode, line.Baseline, line.Compare,
                            line.Baseline, "zero-tolerance"));
                    break;
                case ToleranceBucket.TenPercent:
                    // Aggregate increase over the bucket's baseline
                    // total exceeding 10% violates (§1026.19(e)(3)(ii)).
                    var increase = bucket.Compare - bucket.Baseline;
                    var cap = Math.Round(bucket.Baseline * 0.10m, 2);
                    if (increase > cap)
                        result.Add(new Violation(ToleranceBucket.TenPercent,
                            null, bucket.Baseline, bucket.Compare,
                            bucket.Baseline + cap, "ten-percent-aggregate"));
                    break;
            }
        }
        return result;
    }
}

public enum ToleranceBucket { Zero, TenPercent, Unlimited }
