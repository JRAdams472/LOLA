namespace Lola.Domain.Aus;

/// Vendor-specific category/severity vocabulary -> canonical model.
public static class AusNormalizer
{
    public static AusFindingCategory ClassifyCategory(string? vendorCategory) => vendorCategory?.Trim().ToUpperInvariant() switch
    {
        "CREDIT" => AusFindingCategory.Credit,
        "INCOME" or "EMPLOYMENT" => AusFindingCategory.Income,
        "ASSET" or "ASSETS" or "FUNDS" => AusFindingCategory.Asset,
        "COLLATERAL" or "PROPERTY" or "APPRAISAL" => AusFindingCategory.Collateral,
        "ELIGIBILITY" or "PRODUCT" or "LOAN" => AusFindingCategory.Eligibility,
        _ => AusFindingCategory.Other,
    };

    public static AusFindingSeverity ClassifySeverity(string? vendorSeverity) => vendorSeverity?.Trim().ToUpperInvariant() switch
    {
        "FATAL" or "ERROR" or "HARDSTOP" => AusFindingSeverity.Fatal,
        "REQUIRED" or "DOCUMENTATION" or "CONDITION" => AusFindingSeverity.Required,
        "WARNING" or "CAUTION" or "ALERT" => AusFindingSeverity.Warning,
        _ => AusFindingSeverity.Info,
    };
}
