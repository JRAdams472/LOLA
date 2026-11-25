using System.Text.RegularExpressions;
using Lola.Domain.Applications;
using Lola.Domain.Compliance;

namespace Lola.Application.Compliance;

/// Maps application state to the `AtrQm` battery input (M22 P2,
/// MPE-307): doc paths inside the cipher boundary supply the loan
/// terms + income/debt sums; the latest completed fee eval supplies
/// the points-and-fees lines, classified through `pf_classes` with
/// fail-closed 'counted' for unregistered codes. `PfLines` is null
/// when no completed eval exists — the battery warns rather than
/// guessing zero fees.
public static class AtrQmEvaluator
{
    private static readonly Regex IncomePath = new(
        @"^party\[\d+\]\.employment\[\d+\]\.monthly_income$",
        RegexOptions.Compiled);
    private static readonly Regex DebtPath = new(
        @"^liability\[\d+\]\.monthly_payment$",
        RegexOptions.Compiled);

    /// `items`/`overrides`/`hasEval` describe the latest completed fee
    /// eval the caller resolved; `pfClasses`/`apor`/`state` come from
    /// the registry tables. Overrides (non-stale) win over computed
    /// totals — P&F must reflect what the borrower actually pays.
    public static AtrQm.Input Map(
        ApplicationDocument doc,
        bool hasEval,
        IReadOnlyList<Fees.FeeItemRecord>? items,
        IReadOnlyList<Fees.FeeOverrideRecord> overrides,
        IReadOnlyDictionary<string, PfClassRecord> pfClasses,
        AporRateRecord? apor,
        StateHighCostRecord? state,
        string variant = "")
    {
        IReadOnlyList<AtrQm.PfLine>? pfLines = null;
        if (hasEval && items is not null)
        {
            pfLines = items.Select(i =>
            {
                var category = pfClasses.TryGetValue(i.FeeCode, out var cls)
                    ? cls.Category : "counted"; // fail closed
                var amount = overrides.FirstOrDefault(o =>
                        o.FeeCode == i.FeeCode && o.Section == i.Section
                        && o.Stale == 0 && o.ClearedUtc is null
                        && o.Variant == variant)
                    is { } ov ? (decimal)ov.Amount : (decimal)i.Total;
                return new AtrQm.PfLine(i.FeeCode, amount, category);
            }).ToList();
        }

        decimal income = 0, debts = 0;
        var anyIncome = false; var anyDebt = false;
        foreach (var (path, node) in doc.Leaves())
        {
            if (IncomePath.IsMatch(path) && TryDec(node, out var inc))
            { income += inc; anyIncome = true; }
            else if (DebtPath.IsMatch(path) && TryDec(node, out var dbt))
            { debts += dbt; anyDebt = true; }
        }

        return new AtrQm.Input(
            LoanAmount: Num(doc, "loan.base_amount")
                ?? Num(doc, "loan_amount"),
            NoteRatePct: Num(doc, "interest_rate"),
            TermMonths: Int(doc, "loan_term_months"),
            AmortizationType: Str(doc, "amortization_type"),
            LienPosition: Str(doc, "loan.lien_position"),
            ProductType: Str(doc, "loan.product_type"),
            MonthlyIncome: anyIncome ? income : null,
            MonthlyDebts: anyDebt ? debts : null,
            Apor: (decimal?)apor?.Apor,
            PfLines: pfLines,
            State: state is null ? null : new AtrQm.StateRule(
                state.StateCode, (decimal)state.RateSpread,
                (decimal)state.PfPct, (decimal)state.PfFloor));
    }

    /// Serialize the battery output for `atr_results.verdicts_json` —
    /// flat [{test, verdict, detail}] rows, lower-cased verdict names.
    public static string VerdictsJson(
        IReadOnlyList<AtrQm.Result> results) =>
        System.Text.Json.JsonSerializer.Serialize(results.Select(r =>
            new AtrVerdictDto(r.Test,
                r.Verdict.ToString().ToLowerInvariant(), r.Detail)));

    public static IReadOnlyList<AtrVerdictDto> ParseVerdicts(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer
                .Deserialize<List<AtrVerdictDto>>(json,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static string? Str(ApplicationDocument d, string path)
    {
        var node = d.Get(path);
        if (node is null) return null;
        return node is System.Text.Json.Nodes.JsonValue v
            && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
    }

    private static decimal? Num(ApplicationDocument d, string path)
        => decimal.TryParse(Str(d, path), out var v) ? v : null;

    private static int? Int(ApplicationDocument d, string path)
        => decimal.TryParse(Str(d, path), out var v)
            ? (int)Math.Round(v) : null;

    private static bool TryDec(System.Text.Json.Nodes.JsonNode node,
        out decimal value)
    {
        var text = node is System.Text.Json.Nodes.JsonValue v
            && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
        return decimal.TryParse(text, out value);
    }
}
