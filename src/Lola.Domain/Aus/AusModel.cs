namespace Lola.Domain.Aus;

/// Normalized AUS result — vendor-neutral. Carries no borrower PII beyond
/// what the conditions machinery already handles; raw wire XML stays in
/// the tenant-boundary table, never in this model's events (invariant 3).
public sealed record AusFinding(
    string VendorCode,
    AusFindingCategory Category,
    AusFindingSeverity Severity,
    string Message);

public enum AusFindingCategory { Credit, Income, Asset, Collateral, Eligibility, Other }
public enum AusFindingSeverity { Info, Warning, Required, Fatal }

public enum AusRecommendation
{
    ApproveEligible,   // DU Approve/Eligible, LPA Accept
    Accept,            // LPA Accept when distinguished
    Refer,             // DU Refer / Refer with Caution
    Caution,           // LPA Caution
    Ineligible,
    UnableToEvaluate,  // malformed/incomplete response — never silent-pass
}

public sealed record AusResult(
    string Vendor,              // "du" | "lpa"
    AusRecommendation Recommendation,
    string? RiskClass,
    string? CaseId,             // DU casefile id / LPA key
    IReadOnlyList<AusFinding> Findings);
