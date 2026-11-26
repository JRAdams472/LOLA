using System.Text.RegularExpressions;
using Lola.Domain.Applications;
using Lola.Domain.Compliance;

namespace Lola.Application.Compliance;

/// Maps application state to the RESPA engines' inputs (M23 P2,
/// MPE-310): doc paths inside the cipher boundary supply the escrow
/// account figures and the Section 8 settlement-provider affiliation
/// flags. Missing inputs stay null — the engine returns `incomplete`
/// rather than guessing.
public static class RespaEvaluator
{
    private static readonly Regex AfbaPath = new(
        @"^settlement_provider\[\d+\]\.affiliated_with_lender$",
        RegexOptions.Compiled);

    public static EscrowAnalysis.Input MapEscrow(ApplicationDocument doc)
        => new(
            CurrentBalance: Num(doc, "escrow.current_balance"),
            MonthlyPayment: Num(doc, "escrow.monthly_payment"),
            AnnualPropertyTax: Num(doc, "escrow.annual_property_tax"),
            AnnualHazardInsurance: Num(doc, "escrow.annual_hazard_insurance"),
            AnnualPmi: Num(doc, "escrow.annual_pmi"),
            AnnualHoa: Num(doc, "escrow.annual_hoa"),
            CushionMonths: Int(doc, "escrow.cushion_months"));

    /// Section 8 (§1024.15): any settlement provider flagged
    /// affiliated with the lender → the AfBA disclosure is required.
    public static bool AfbaRequired(ApplicationDocument doc)
    {
        foreach (var (path, node) in doc.Leaves())
        {
            if (!AfbaPath.IsMatch(path)) continue;
            var text = node is System.Text.Json.Nodes.JsonValue v
                && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
            if (text is "true" or "True" or "yes" or "1") return true;
        }
        return false;
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
        => int.TryParse(Str(d, path), out var v) ? v : null;
}
