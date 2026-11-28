using System.Globalization;

namespace Lola.Domain.Credit;

/// Tri-merge merge engine (M26) — pure functions over parsed bureau
/// files. Rules are explicit and test-pinned:
///
/// 1. **Dedupe** — tradelines across bureau files are the same account
///    when (normalized creditor, account mask) agree. Creditor
///    normalization uppercases, strips punctuation, and collapses
///    whitespace ("CAPITAL ONE" ≡ "Capital One, N.A."-class spellings
///    only when the mask agrees — name alone never merges).
/// 2. **Conflict resolution** — most-recent-reporting wins for balance
///    and monthly payment; worst-status wins for delinquency status.
/// 3. **Late events union** — the same (month, severity) event reported
///    by multiple bureaus counts once; the worst severity per month wins.
/// 4. **Representative score** — middle of three, lower of two, sole
///    of one; score-model tags are preserved per bureau file and NEVER
///    averaged across models.
public static class TriMergeMerger
{
    public static MergedCreditReport Merge(IReadOnlyList<BureauFile> files)
    {
        var groups = new Dictionary<string, List<(BureauFile file, BureauTradeline tl)>>(
            StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var file in files)
        {
            foreach (var tl in file.Tradelines)
            {
                var key = DedupeKey(tl);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                    order.Add(key);
                }
                list.Add((file, tl));
            }
        }

        var merged = new List<MergedTradeline>();
        foreach (var key in order)
        {
            var entries = groups[key];
            // Most-recent-reporting carries balance/payment.
            var latest = entries
                .OrderByDescending(e => e.tl.LastReportedDate ?? "", StringComparer.Ordinal)
                .First();
            var worstStatus = entries
                .Select(e => e.tl.Status)
                .Where(s => s is not null)
                .OrderByDescending(StatusSeverity)
                .FirstOrDefault();
            var lates = UnionLates(entries.SelectMany(e => e.tl.Lates));
            merged.Add(new MergedTradeline(
                latest.tl.Creditor,
                entries.Select(e => e.tl.AccountMask)
                    .FirstOrDefault(m => m is not null),
                latest.tl.Type,
                latest.tl.Balance,
                latest.tl.MonthlyPayment,
                worstStatus,
                latest.tl.LastReportedDate,
                entries.Select(e => e.file.Bureau).Distinct().Order().ToList(),
                lates));
        }

        var scores = files
            .Select(f => new BureauScore(f.Bureau, f.Score, f.ScoreModel))
            .ToList();
        var (repScore, repModel) = Representative(scores);
        var (l30, l60, l90) = CountLates(merged, LookbackCutoff());

        return new MergedCreditReport(scores, repScore, repModel,
            merged, l30, l60, l90);
    }

    /// 24-month lookback from today — test-injectable via the overload.
    public static (int l30, int l60, int l90) CountLates(
        IReadOnlyList<MergedTradeline> tradelines, string cutoffMonth)
    {
        int l30 = 0, l60 = 0, l90 = 0;
        foreach (var tl in tradelines)
        {
            foreach (var late in tl.Lates)
            {
                if (string.CompareOrdinal(late.Month, cutoffMonth) < 0)
                    continue;
                if (late.Severity >= 90) l90++;
                else if (late.Severity >= 60) l60++;
                else if (late.Severity >= 30) l30++;
            }
        }
        return (l30, l60, l90);
    }

    public static string LookbackCutoff()
        => DateTime.UtcNow.AddMonths(-24).ToString("yyyy-MM",
            CultureInfo.InvariantCulture);

    /// Same (month) reported by several bureaus counts once — the
    /// worst severity wins for that month.
    internal static IReadOnlyList<LateEvent> UnionLates(
        IEnumerable<LateEvent> events)
        => events
            .GroupBy(e => e.Month, StringComparer.Ordinal)
            .Select(g => new LateEvent(g.Key, g.Max(e => e.Severity)))
            .OrderBy(e => e.Month, StringComparer.Ordinal)
            .ToList();

    /// Mid-of-three, lower-of-two, sole-of-one. The representative
    /// model is the model OF THE CHOSEN BUREAU FILE — never blended.
    internal static (int? score, string? model) Representative(
        IReadOnlyList<BureauScore> scores)
    {
        var valid = scores.Where(s => s.Score is not null)
            .OrderBy(s => s.Score!.Value).ToList();
        return valid.Count switch
        {
            0 => (null, null),
            1 => (valid[0].Score, valid[0].Model),
            2 => (valid[0].Score, valid[0].Model),   // lower of two
            _ => (valid[1].Score, valid[1].Model),   // middle of three+
        };
    }

    internal static string DedupeKey(BureauTradeline tl)
    {
        var mask = tl.AccountMask?.Trim() ?? "";
        return mask.Length > 0
            ? $"mask:{mask}"
            : $"name:{NormalizeCreditor(tl.Creditor)}|{tl.Type}";
    }

    /// Name-only normalization — uppercase, strip punctuation, collapse
    /// whitespace. Used for liability matching and as a weak dedupe
    /// fallback when no mask is reported.
    public static string NormalizeCreditor(string creditor)
    {
        var chars = creditor.ToUpperInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();
        // Trailing corporate-form tokens carry no matching signal —
        // bureaus report the same creditor with and without them.
        var words = new string(chars)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && CorporateTokens.Contains(words[^1]))
            words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    private static readonly HashSet<string> CorporateTokens = new(
        StringComparer.Ordinal)
    {
        "NA", "INC", "LLC", "CORP", "CORPORATION", "LTD", "CO",
    };

    internal static int StatusSeverity(string? status) => status switch
    {
        "chargeoff" => 120,
        "collection" => 110,
        "120" => 100,
        "90" => 90,
        "60" => 60,
        "30" => 30,
        "current" => 1,
        "closed" => 0,
        _ => -1,
    };
}
