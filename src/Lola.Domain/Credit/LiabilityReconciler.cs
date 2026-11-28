namespace Lola.Domain.Credit;

/// Liability reconciliation (M26, user-directed scope) — matches merged
/// tri-merge tradelines against borrower-entered `liability[i]` doc
/// entries and produces doc writes. Rules are explicit and test-pinned:
///
/// 1. **Match** — exact masked-account match first (digit tails, 4+
///    digits); otherwise normalized-creditor equality AND monthly
///    payment within 10% of each other. A tradeline matching nothing
///    is bureau-discovered debt.
/// 2. **Matched** — balance and monthly payment UPDATE to the
///    bureau-reported values (when the bureau reports them); the
///    borrower entry is never otherwise rewritten.
/// 3. **Unmatched** — APPEND a new liability entry carrying the
///    tradeline data + `source = tri_merge` so officers can tell
///    bureau-discovered debt from borrower-declared.
/// 4. **Never delete** — borrower-entered liabilities are never
///    removed, even when no tradeline matches them.
///
/// Output is a path→value write list — the completion handler applies
/// it as ONE new append-only doc version (invariant 6).
public static class LiabilityReconciler
{
    /// payment tolerance for creditor-name matching — 10% of the
    /// borrower-entered payment, minimum $25 (small payments drift).
    public static decimal PaymentTolerance(decimal payment)
        => Math.Max(25m, payment * 0.10m);

    public sealed record DocLiability(
        int Index, string? Type, string? Creditor, string? AccountNumber,
        decimal? MonthlyPayment, decimal? Balance);

    public sealed record Write(string Path, string Value);

    public static IReadOnlyList<Write> Reconcile(
        IReadOnlyList<DocLiability> existing,
        IReadOnlyList<MergedTradeline> tradelines)
    {
        var writes = new List<Write>();
        var matched = new HashSet<int>();
        var nextIndex = existing.Count == 0
            ? 0 : existing.Max(e => e.Index) + 1;

        foreach (var tl in tradelines)
        {
            var match = FindMatch(existing, matched, tl);
            if (match is not null)
            {
                matched.Add(match.Index);
                if (tl.Balance is { } bal)
                    writes.Add(new Write(
                        $"liability[{match.Index}].balance",
                        bal.ToString("0.##")));
                if (tl.MonthlyPayment is { } pay)
                    writes.Add(new Write(
                        $"liability[{match.Index}].monthly_payment",
                        pay.ToString("0.##")));
            }
            else
            {
                var i = nextIndex++;
                writes.Add(new Write(
                    $"liability[{i}].liability_type", MapType(tl.Type)));
                writes.Add(new Write(
                    $"liability[{i}].creditor", tl.Creditor));
                if (tl.AccountMask is { } mask)
                    writes.Add(new Write(
                        $"liability[{i}].account_number", mask));
                if (tl.MonthlyPayment is { } pay)
                    writes.Add(new Write(
                        $"liability[{i}].monthly_payment",
                        pay.ToString("0.##")));
                if (tl.Balance is { } bal)
                    writes.Add(new Write(
                        $"liability[{i}].balance", bal.ToString("0.##")));
                writes.Add(new Write(
                    $"liability[{i}].source", "tri_merge"));
            }
        }
        return writes;
    }

    /// Late-count normalization — separate writes so the completion
    /// handler can compose one version.
    public static IReadOnlyList<Write> LateWrites(
        int lates30, int lates60, int lates90) =>
    [
        new Write("credit.lates_30", lates30.ToString()),
        new Write("credit.lates_60", lates60.ToString()),
        new Write("credit.lates_90", lates90.ToString()),
    ];

    internal static DocLiability? FindMatch(
        IReadOnlyList<DocLiability> existing,
        HashSet<int> alreadyMatched,
        MergedTradeline tl)
    {
        // Exact masked-account match — digit tail comparison, 4+ digits.
        var tlDigits = DigitTail(tl.AccountMask);
        if (tlDigits is not null)
        {
            var byAccount = existing.FirstOrDefault(e =>
                !alreadyMatched.Contains(e.Index)
                && DigitTail(e.AccountNumber) is { } docDigits
                && docDigits == tlDigits);
            if (byAccount is not null) return byAccount;
        }
        // Creditor-name + payment-tolerance match.
        var tlCreditor = TriMergeMerger.NormalizeCreditor(tl.Creditor);
        return existing.FirstOrDefault(e =>
            !alreadyMatched.Contains(e.Index)
            && e.Creditor is not null
            && TriMergeMerger.NormalizeCreditor(e.Creditor) == tlCreditor
            && e.MonthlyPayment is { } pay
            && tl.MonthlyPayment is { } tlPay
            && Math.Abs(pay - tlPay) <= PaymentTolerance(pay));
    }

    /// Digit tail of a masked account — bureaus mask differently
    /// ("****1234" vs "xxx1234" vs "1234"); compare on the trailing
    /// digit run when it carries 4+ digits.
    internal static string? DigitTail(string? mask)
    {
        if (mask is null) return null;
        var digits = new string(mask.Reverse()
            .TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length >= 4 ? digits : null;
    }

    internal static string MapType(string tradelineType) => tradelineType switch
    {
        "mortgage" => "mortgage",
        "auto" => "auto",
        "installment" => "installment",
        "revolving" => "revolving",
        "student" => "installment",
        _ => "other",
    };
}
