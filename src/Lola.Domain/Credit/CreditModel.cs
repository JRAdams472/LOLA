namespace Lola.Domain.Credit;

/// Normalized per-bureau credit file (M26) — the parser output shape
/// independent of any vendor wire format. Tradelines carry explicit
/// late events so merge/reconciliation logic never re-derives dates
/// from payment-history grids.
public sealed record BureauFile(
    string Bureau,                       // 'equifax' | 'transunion' | 'experian'
    int? Score,
    string? ScoreModel,                  // 'fico-10t' | 'vantagescore-4.0' | vendor string
    IReadOnlyList<BureauTradeline> Tradelines);

public sealed record BureauTradeline(
    string Creditor,
    string? AccountMask,                 // masked/truncated account as reported
    string Type,                         // mortgage|revolving|installment|auto|student|other
    string? OpenedDate,
    decimal? Balance,
    decimal? MonthlyPayment,
    string? Status,                      // current|30|60|90|120|collection|chargeoff|closed
    string? LastReportedDate,            // yyyy-MM-dd — conflict resolution clock
    IReadOnlyList<LateEvent> Lates);

/// One delinquency occurrence — severity is the worst rating reached
/// in that month per the bureau's payment grid.
public sealed record LateEvent(string Month, int Severity);

/// Merged tri-merge output: per-bureau scores preserved WITH model
/// tags (never averaged across models), tradelines deduped across
/// bureau files with lineage, late events unioned.
public sealed record MergedCreditReport(
    IReadOnlyList<BureauScore> Scores,
    int? RepresentativeScore,
    string? RepresentativeModel,
    IReadOnlyList<MergedTradeline> Tradelines,
    int Lates30, int Lates60, int Lates90);

public sealed record BureauScore(
    string Bureau, int? Score, string? Model);

public sealed record MergedTradeline(
    string Creditor,
    string? AccountMask,
    string Type,
    decimal? Balance,
    decimal? MonthlyPayment,
    string? Status,
    string? LastReportedDate,
    IReadOnlyList<string> Bureaus,       // lineage — who reported it
    IReadOnlyList<LateEvent> Lates);
