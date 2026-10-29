namespace Lola.Application.Pricing;

/// Pricing/eligibility adapter seam (ADR-0007, MPE-71). Core LOS logic
/// talks ONLY to this interface — MPEx is the first implementation;
/// future vendors (Optimal Blue et al.) arrive as a DTS-style transform
/// behind the same contract with zero core changes.
///
/// The request side is the canonical PII-free scenario: loan/property/
/// borrower/scenario attributes only — never identity data (invariant
/// 2, enforced by the reflection firewall test).
public interface IPricingService
{
    /// Eligibility + pricing for one scenario. `asOf` selects the
    /// publication snapshot: null = latest, "draft", or an RFC3339
    /// timestamp for historic re-evaluation.
    Task<PricingEvaluation> EvaluateAsync(
        PricingScenario scenario, PricingRequestOptions options,
        CancellationToken ct = default);
}

/// Canonical PII-free request — mirrors MPEx's LoanScenario shape
/// (camelCase on the wire). `CreditModel` mirrors MPEx's typed enum —
/// FICO/VantageScore/F10T, or "Other" for unknown model strings.
public sealed record PricingScenario(
    LoanSection Loan,
    PropertySection Property,
    BorrowerSection Borrower,
    ScenarioSection Scenario);

public sealed record LoanSection(
    string? Purpose, string? Type, decimal? Amount, int? TermMonths,
    string? Amortization, bool? InterestOnly, decimal? Ltv,
    decimal? Cltv, int? Units);

public sealed record PropertySection(
    string? Type, string? Occupancy, string? State,
    decimal? AppraisedValue, decimal? PurchasePrice);

public sealed record BorrowerSection(
    int? CreditScore, string? CreditModel, bool? FirstTimeBuyer,
    bool? SelfEmployed);

public sealed record ScenarioSection(int? LockTermDays, string? AsOf);

/// Caller options orthogonal to the scenario (eval-time knobs).
public sealed record PricingRequestOptions(
    IReadOnlyList<string>? Products = null,
    decimal? Rate = null,
    int? LockTermDays = null,
    string? AsOf = null,
    bool EligibilityOnly = false);

/// Normalized inbound result — the standardized LOS list (ticket:
/// "eligible products, rates, margins, lock periods").
public sealed record PricingEvaluation(
    string EvalId, string Adapter, string? AsOf, string EvaluatedUtc,
    IReadOnlyList<PricedProduct> Products);

public sealed record PricedProduct(
    string ProductCode, string? ProductName, bool Eligible,
    IReadOnlyList<string> IneligibilityReasons,
    IReadOnlyList<RateOffer> Offers);

public sealed record RateOffer(
    decimal Rate, decimal? Price, decimal? Margin,
    int? LockTermDays, string? InvestorName);
