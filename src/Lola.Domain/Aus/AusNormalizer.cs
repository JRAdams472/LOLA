namespace Lola.Domain.Aus;

/// <summary>
/// Vendor-specific category/severity vocabulary → canonical model.
/// DU MessageType values are compound tokens ("REP-CREDIT-SCORE LIST");
/// LPA group types are PascalCase ("DocumentationMessages") — matching is
/// substring-based and conservative, defaulting to Other/Info.
/// </summary>
public static class AusNormalizer
{
    public static AusFindingCategory ClassifyCategory(string? vendorCategory)
    {
        var v = vendorCategory?.Trim().ToUpperInvariant() ?? "";
        if (v.Contains("CREDIT") || v.Contains("SCORE")) return AusFindingCategory.Credit;
        if (v.Contains("INCOME") || v.Contains("EMPLOYMENT")) return AusFindingCategory.Income;
        if (v.Contains("ASSET") || v.Contains("FUNDS")) return AusFindingCategory.Asset;
        if (v.Contains("COLLATERAL") || v.Contains("PROPERTY") || v.Contains("APPRAISAL")) return AusFindingCategory.Collateral;
        if (v.Contains("ELIGIBILITY") || v.Contains("PRODUCT") || v.Contains("LOAN")) return AusFindingCategory.Eligibility;
        return AusFindingCategory.Other;
    }

    public static AusFindingSeverity ClassifySeverity(string? vendorSeverity) => vendorSeverity?.Trim().ToUpperInvariant() switch
    {
        "FATAL" or "ERROR" or "HARDSTOP" => AusFindingSeverity.Fatal,
        "REQUIRED" or "DOCUMENTATION" or "CONDITION" => AusFindingSeverity.Required,
        "WARNING" or "CAUTION" or "ALERT" => AusFindingSeverity.Warning,
        _ => AusFindingSeverity.Info,   // DU "Observation" and nulls land here
    };
}
