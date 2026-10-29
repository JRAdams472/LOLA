using System.Text.Json.Nodes;
using Lola.Domain.Applications;

namespace Lola.Application.Pricing;

/// Maps a 1003 application document to the canonical PII-free
/// <see cref="PricingScenario"/> (MPE-71, invariant 2). Only
/// loan/scenario attributes cross — identity fields (names, SSN, DOB,
/// contact, account numbers) are structurally absent because no catalog
/// path for them appears below; the firewall test enforces it.
public static class ScenarioMapper
{
    // Canonical path → egress member, with enum normalization into the
    // vocabulary MPEx rules expect. Unmapped catalog values emit as
    // absent (null), never passed through raw — a wrong enum is worse
    // than a missing one for eligibility decisions.
    public static PricingScenario Map(
        ApplicationDocument doc, int partyIndex = 0,
        int? lockTermDays = null, string? asOf = null)
    {
        var p = $"party[{partyIndex}]";
        var loan = new LoanSection(
            Purpose: MapEnum(Str(doc, "loan_purpose"), PurposeMap),
            Type: MapEnum(Str(doc, "loan.product_type"), ProductMap),
            Amount: Num(doc, "loan.base_amount") ?? Num(doc, "loan_amount"),
            TermMonths: Int(doc, "loan_term_months"),
            Amortization: MapEnum(Str(doc, "amortization_type"), AmortMap),
            InterestOnly: null,
            Ltv: Num(doc, "loan.ltv"),
            Cltv: Num(doc, "loan.cltv"),
            Units: Int(doc, "subject_property.units"));

        var property = new PropertySection(
            Type: MapEnum(Str(doc, "subject_property.property_type"), PropMap),
            Occupancy: MapEnum(Str(doc, "subject_property.usage"), OccupMap),
            State: Str(doc, "subject_property.state"),
            AppraisedValue: Num(doc, "subject_property.est_value"),
            PurchasePrice: null);

        var borrower = new BorrowerSection(
            CreditScore: Int(doc, $"{p}.credit_score"),
            CreditModel: MapEnum(Str(doc, $"{p}.credit_model"), CreditModelMap),
            FirstTimeBuyer: Bool(doc, "declaration.first_time_buyer"),
            SelfEmployed: Bool(doc, $"{p}.employment[0].self_employed"));

        return new PricingScenario(
            loan, property, borrower,
            new ScenarioSection(lockTermDays, asOf));
    }

    private static readonly IReadOnlyDictionary<string, string> PurposeMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["purchase"] = "purchase",
            ["refinance"] = "rateTermRefi",
            ["rate_term"] = "rateTermRefi",
            ["cash_out"] = "cashOutRefi",
            ["cash_out_refinance"] = "cashOutRefi"
        };

    private static readonly IReadOnlyDictionary<string, string> ProductMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["conventional"] = "conventional",
            ["fha"] = "fha",
            ["va"] = "va",
            ["usda"] = "usda",
            ["jumbo"] = "jumbo"
        };

    private static readonly IReadOnlyDictionary<string, string> AmortMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["fixed"] = "fixed", ["arm"] = "arm" };

    private static readonly IReadOnlyDictionary<string, string> PropMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["single_family"] = "sfr",
            ["sfr"] = "sfr",
            ["condo"] = "condo",
            ["townhouse"] = "townhouse",
            ["multi_unit"] = "multiUnit2_4",
            ["multi_unit_2_4"] = "multiUnit2_4",
            ["two_to_four"] = "multiUnit2_4",
            ["manufactured"] = "manufactured",
            ["pud"] = "pud"
        };

    // Catalog values → the model spellings MPEx's `CreditModel` enum
    // accepts (FICO/VantageScore/F10T; Other passes unknowns through).
    private static readonly IReadOnlyDictionary<string, string> CreditModelMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fico"] = "FICO",
            ["vantage"] = "VantageScore",
            ["vantagescore"] = "VantageScore",
            ["fico10t"] = "F10T",
            ["f10t"] = "F10T",
            ["other"] = "Other"
        };

    private static readonly IReadOnlyDictionary<string, string> OccupMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["primary_residence"] = "primary",
            ["primary"] = "primary",
            ["second_home"] = "secondHome",
            ["second"] = "secondHome",
            ["investment"] = "investment",
            ["investment_property"] = "investment"
        };

    private static string? MapEnum(string? raw, IReadOnlyDictionary<string, string> map)
        => raw is not null && map.TryGetValue(raw.Trim(), out var v) ? v : null;

    private static string? Str(ApplicationDocument d, string path)
        => d.Get(path)?.GetValue<string>() ?? d.Get(path)?.ToString();

    private static decimal? Num(ApplicationDocument d, string path)
        => decimal.TryParse(Str(d, path), out var v) ? v : null;

    private static int? Int(ApplicationDocument d, string path)
        => decimal.TryParse(Str(d, path), out var v) ? (int)Math.Round(v) : null;

    private static bool? Bool(ApplicationDocument d, string path)
        => Str(d, path)?.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "y" or "1" => true,
            "false" or "no" or "n" or "0" => false,
            _ => null
        };
}
