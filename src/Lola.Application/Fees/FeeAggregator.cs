namespace Lola.Application.Fees;

/// Aggregates an eval's `fee_items` rows + the application's live
/// overrides into the section-grouped breakdown (MPE-184). An
/// active (unstale, uncleared) override wins over the computed
/// total for its (feeCode, section); overrides whose fee vanished
/// from the eval surface in StaleOverrides — never silently merged
/// or dropped.
public static class FeeAggregator
{
    public static FeeBreakdownDto ToBreakdown(FeeEvalRecord eval,
        IReadOnlyList<FeeItemRecord> items,
        IReadOnlyList<FeeOverrideRecord> overrides)
    {
        var live = overrides.Where(o => o.ClearedUtc is null).ToList();
        var byKey = live.ToDictionary(
            o => (o.FeeCode, o.Section), StringTupleComparer.Ordinal);

        var sections = items
            .GroupBy(i => i.Section)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var lines = g.Select(i =>
                {
                    byKey.TryGetValue((i.FeeCode, i.Section), out var o);
                    var active = o is { Stale: 0 };
                    return new FeeLineDto(i.ItemId, i.FeeCode, i.Label,
                        (decimal)i.Base, (decimal)i.Total,
                        active ? (decimal)o!.Amount : null,
                        active ? o!.Reason : null,
                        active ? (decimal)o!.Amount : (decimal)i.Total);
                }).ToList();
                return new FeeSectionDto(g.Key, lines,
                    lines.Sum(l => l.Computed),
                    lines.Sum(l => l.Effective));
            }).ToList();

        var stale = live.Where(o => o.Stale != 0)
            .Select(o => new StaleOverrideDto(o.OverrideId, o.FeeCode,
                o.Section, (decimal)o.Amount, o.Reason, o.Actor, o.CreatedUtc))
            .ToList();

        return new FeeBreakdownDto(eval.EvalId, eval.ApplicationId,
            eval.Adapter, eval.Status, eval.Error, sections,
            sections.Sum(s => s.ComputedTotal),
            sections.Sum(s => s.EffectiveTotal),
            stale, eval.CreatedUtc, eval.CompletedUtc);
    }

    private sealed class StringTupleComparer
        : IEqualityComparer<(string, string)>
    {
        public static readonly StringTupleComparer Ordinal = new();
        public bool Equals((string, string) x, (string, string) y)
            => x == y;
        public int GetHashCode((string, string) t) => t.GetHashCode();
    }
}
