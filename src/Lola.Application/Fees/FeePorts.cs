namespace Lola.Application.Fees;

/// Fee-engine adapter seam (ADR-0007, MPE-75). Core LOS logic talks
/// ONLY to this interface — Zara is the first implementation; future
/// fee vendors arrive behind the same contract with zero core
/// changes (same swap story as IPricingService).
///
/// The request side is the minimal PII-free fee scenario:
/// loan/property/borrower attributes needed by TRID fee rules —
/// never identity data (invariant 2, enforced by the reflection
/// firewall over this namespace).
public interface IFeeService
{
    /// Adapter identity stamped onto eval rows ("stub", "zara").
    string Adapter { get; }

    /// Itemized fee evaluation for one scenario.
    Task<FeeEvaluation> EvaluateAsync(
        FeeScenario scenario, FeeRequestOptions options,
        CancellationToken ct = default);
}

/// Minimal PII-free fee request — `{loan, property, borrower}` on the
/// wire (camelCase). Optional extension fields pass through to rules.
public sealed record FeeScenario(
    FeeLoanSection Loan,
    FeePropertySection Property,
    FeeBorrowerSection Borrower);

public sealed record FeeLoanSection(
    string? Purpose, string? Type, decimal? Amount, int? TermMonths);

public sealed record FeePropertySection(
    string? State, string? Zip, string? County,
    decimal? PurchasePrice, decimal? EstValue);

public sealed record FeeBorrowerSection(
    bool? VaEligible, bool? DiscountPoints, bool? SelfEmployed);

/// Eval-time knobs orthogonal to the scenario.
public sealed record FeeRequestOptions(
    IReadOnlyList<string>? FeeCodes = null);

/// Normalized inbound result — one line per fee, grouped into LE
/// cost blocks by `Section` (A Origination, B Cannot Shop, C Can
/// Shop, E Taxes/Gov, F Prepaids — Zara's tridBucket verbatim).
public sealed record FeeEvaluation(
    string EvalId, string Adapter, string EvaluatedUtc,
    string? Fingerprint, IReadOnlyList<FeeLine> Items);

public sealed record FeeLine(
    string FeeCode, string? Label, string Section, decimal Base,
    IReadOnlyList<FeeAdjustment> Adjustments, decimal Total);

public sealed record FeeAdjustment(
    string Rule, string Op, decimal Value, decimal RunningTotal);
