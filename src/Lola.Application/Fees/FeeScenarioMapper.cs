using System.Text.Json.Nodes;
using Lola.Domain.Applications;

namespace Lola.Application.Fees;

/// Maps a 1003 application document to the minimal PII-free
/// <see cref="FeeScenario"/> (MPE-75, invariant 2). Only the fields
/// TRID fee rules need cross — identity data is structurally absent
/// because no catalog path for it appears below; the firewall test
/// enforces it. Zip stays OUT: the fee engine's inbound PII guard
/// deny-lists `zip`/`postal` on the wire, so the eval request would
/// 422 outright — state/county carry the geo signal the rules need.
public static class FeeScenarioMapper
{
    /// The catalog paths that feed the fee scenario — an active fee
    /// eval's input set. Any future fee-field freeze (LE accuracy)
    /// guards this list the way rate locks guard ScenarioMapper's.
    public static readonly IReadOnlyList<string> InputPaths =
    [
        "loan_purpose", "loan.product_type", "loan.base_amount",
        "loan_amount", "loan_term_months", "subject_property.state",
        "subject_property.county",
        "subject_property.est_value",
        "subject_property.purchase_price", "declaration.va_eligible",
        "declaration.discount_points",
        "party[0].employment[0].self_employed",
    ];

    public static FeeScenario Map(ApplicationDocument doc,
        int partyIndex = 0)
    {
        var p = $"party[{partyIndex}]";
        return new FeeScenario(
            new FeeLoanSection(
                Purpose: Str(doc, "loan_purpose"),
                Type: Str(doc, "loan.product_type"),
                Amount: Num(doc, "loan.base_amount") ?? Num(doc, "loan_amount"),
                TermMonths: Int(doc, "loan_term_months")),
            new FeePropertySection(
                State: Str(doc, "subject_property.state"),
                // Denied by the engine's inbound PII guard — omit.
                Zip: null,
                County: Str(doc, "subject_property.county"),
                PurchasePrice: Num(doc, "subject_property.purchase_price"),
                EstValue: Num(doc, "subject_property.est_value")),
            new FeeBorrowerSection(
                VaEligible: Bool(doc, "declaration.va_eligible"),
                DiscountPoints: Bool(doc, "declaration.discount_points"),
                SelfEmployed: Bool(doc, $"{p}.employment[0].self_employed")));
    }

    private static string? Str(ApplicationDocument d, string path)
        => d.Get(path)?.GetValue<string>() ?? d.Get(path)?.ToString();

    private static decimal? Num(ApplicationDocument d, string path)
        => decimal.TryParse(Str(d, path), out var v) ? v : null;

    private static int? Int(ApplicationDocument d, string path)
        => decimal.TryParse(Str(d, path), out var v)
            ? (int)Math.Round(v) : null;

    private static bool? Bool(ApplicationDocument d, string path)
        => Str(d, path)?.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "y" or "1" => true,
            "false" or "no" or "n" or "0" => false,
            _ => null
        };
}
