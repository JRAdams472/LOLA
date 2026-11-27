namespace Lola.Domain.Hmda;

/// HMDA edit-check engine — three tiers per the FIG Section 5:
/// syntactical (row shape/types), validity (V-series per-field), and
/// quality (Q-series cross-field plausibility). Each edit carries the
/// CFPB edit id it implements.
///
/// Coverage note (per docs/m24-plan.md): this is the core battery, not
/// the full ~250-edit matrix. Implemented edits are listed in `All`;
/// the uncovered remainder is a named close-out item.
public sealed record EditResult(string EditId, string Tier, string Result, string Detail);

public static class EditChecks
{
    public sealed record Edit(string Id, string Tier, Func<IReadOnlyDictionary<string, string>, (bool fail, string detail)> Check);

    /// The battery — evaluated in declared order. `fail=true` means the
    /// edit FIRES (data problem); false means pass.
    public static readonly IReadOnlyList<Edit> All =
    [
        // ---- syntactical ----
        new("S300", "syntactical", r => (!r.ContainsKey("record_identifier") || r["record_identifier"] != "2",
            "record identifier missing or not 2")),
        new("S301", "syntactical", r => (r.Count < LarSchema.ExportColumns.Count / 2,
            "row materially incomplete — fewer than half the schema columns populated")),
        new("S302", "syntactical", r => (r.TryGetValue("lei", out var lei) && lei.Length != 20,
            "LEI must be exactly 20 characters")),

        // ---- validity (V-series battery) ----
        new("V608", "validity", r => (r.TryGetValue("uli", out var uli) && uli.Length >= 23 && !Uli.Validate(uli),
            "ULI check digit invalid")),
        new("V610", "validity", r => (!ValidDate(r, "application_date"),
            "application date missing or malformed (want yyyyMMdd or NA)")),
        new("V610b", "validity", r => (!ValidDate(r, "action_taken_date"),
            "action taken date missing or malformed (want yyyyMMdd or NA)")),
        new("V615", "validity", r => (!Num(r, "loan_amount"),
            "loan amount must be numeric or NA")),
        new("V617", "validity", r => (!In(r, "loan_type", "1", "2", "3", "4", "NA"),
            "loan type must be 1-4")),
        new("V618", "validity", r => (!In(r, "loan_purpose", "1", "2", "31", "32", "4", "5", "NA"),
            "loan purpose code invalid")),
        new("V619", "validity", r => (!In(r, "action_taken", "1", "2", "3", "4", "5", "6", "7", "8"),
            "action taken must be 1-8")),
        new("V621", "validity", r => (!In(r, "occupancy_type", "1", "2", "3"),
            "occupancy type must be 1-3")),
        new("V628", "validity", r => (!In(r, "ethnicity_1", "1", "11", "12", "13", "14", "2", "3", "4", "NA"),
            "ethnicity 1 code invalid")),
        new("V630", "validity", r => (!In(r, "race_1", "1", "2", "21", "22", "23", "24", "25", "26", "27", "3", "4", "41", "42", "43", "44", "5", "6", "7", "NA"),
            "race 1 code invalid")),
        new("V633", "validity", r => (!In(r, "sex", "1", "2", "3", "4", "NA"),
            "sex code invalid")),
        new("V638", "validity", r => (r.TryGetValue("age", out var age) && age != "NA" && age != "8888"
                && (!int.TryParse(age, out var a) || a < 0 || a > 130),
            "age must be a plausible number of years or NA")),
        new("V651", "validity", r => (!Num(r, "combined_loan_to_value"),
            "CLTV must be numeric or NA")),
        new("V654", "validity", r => (!Num(r, "income"),
            "income must be numeric ($000s) or NA")),
        new("V657", "validity", r => (!Num(r, "rate_of_interest"),
            "rate of interest must be numeric or NA")),
        new("V660", "validity", r => (!Num(r, "rate_spread"),
            "rate spread must be numeric or NA")),
        new("V662", "validity", r => (!In(r, "hoepa_status", "1", "2", "NA"),
            "HOEPA status must be 1 (high-cost) or 2 (not high-cost)")),
        new("V696", "validity", r => (!In(r, "aus_1", "1", "2", "3", "4", "5", "6", "NA") && !In(r, "aus_1", "1111"),
            "AUS 1 code invalid")),

        // ---- quality (Q-series battery) ----
        new("Q600", "quality", r => Cross(r, "loan_amount", "property_value",
            (amt, val) => amt > 0 && val > 0 && amt / val > 3m,
            "loan amount exceeds 300% of property value — verify CLTV/amount consistency")),
        new("Q603", "quality", r => (r.TryGetValue("rate_of_interest", out var ri) && ri != "NA"
                && decimal.TryParse(ri, out var rate) && rate > 25m,
            "interest rate above 25% — verify accuracy")),
        new("Q608", "quality", r => (r.TryGetValue("action_taken", out var at) && at == "1"
                && r.TryGetValue("rate_spread", out var rs) && decimal.TryParse(rs, out var spread) && spread == 0m,
            "originated loan with exactly zero rate spread — verify APR/APOR inputs")),
        new("Q616", "quality", r => Cross(r, "combined_loan_to_value", "loan_amount",
            (cltv, amt) => cltv > 0 && cltv < 20m && amt > 1_000_000m,
            "very low CLTV on a large loan — plausibility check")),
        new("Q622", "quality", r => (r.TryGetValue("action_taken", out var at) && at == "5"
                && r.TryGetValue("action_taken_date", out var d) && d == "NA",
            "action taken 5 (incomplete) requires a valid action taken date")),
        new("Q640", "quality", r => Cross(r, "income", "loan_amount",
            (income, amt) => income > 0 && income < 5m && amt > 500_000m,
            "income under $5k with loan above $500k — plausibility check")),
    ];

    /// Runs the full battery against an assembled row. Every edit emits
    /// exactly one result — pass/fail/warn — so the run is
    /// reconstruction-complete: consumers can count the battery.
    public static IReadOnlyList<EditResult> Run(IReadOnlyDictionary<string, string> row)
    {
        var results = new List<EditResult>(All.Count);
        foreach (var e in All)
        {
            var (fail, detail) = e.Check(row);
            results.Add(new EditResult(e.Id, e.Tier,
                fail ? (e.Tier == "quality" ? "warn" : "fail") : "pass",
                fail ? detail : ""));
        }
        return results;
    }

    private static bool ValidDate(IReadOnlyDictionary<string, string> r, string key)
    {
        if (!r.TryGetValue(key, out var v)) return false;
        if (v == "NA") return true;
        return v.Length == 8 && int.TryParse(v, out _)
            && DateOnly.TryParseExact(v, "yyyyMMdd", out _);
    }

    private static bool Num(IReadOnlyDictionary<string, string> r, string key) =>
        r.TryGetValue(key, out var v) && (v == "NA" || v == "Exempt" || decimal.TryParse(v, out _));

    private static bool In(IReadOnlyDictionary<string, string> r, string key, params string[] valid) =>
        r.TryGetValue(key, out var v) && valid.Contains(v);

    /// Cross-field numeric plausibility: the edit fires when both
    /// fields parse as decimals AND the predicate holds; absent or
    /// NA fields never fire a quality edit.
    private static (bool, string) Cross(IReadOnlyDictionary<string, string> r, string keyA, string keyB,
        Func<decimal, decimal, bool> predicate, string detail)
    {
        if (!r.TryGetValue(keyA, out var a) || !decimal.TryParse(a, out var va)) return (false, detail);
        if (!r.TryGetValue(keyB, out var b) || !decimal.TryParse(b, out var vb)) return (false, detail);
        return (predicate(va, vb), detail);
    }
}
