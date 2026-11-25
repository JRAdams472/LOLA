namespace Lola.Application.Compliance;

/// ATR/QM persistence seam (M22 P2, MPE-307). Rows live in the tenant
/// file — `atr_results` is the append-only evaluation read surface
/// (never updated; a re-run inserts a new row); `apor_rates`,
/// `pf_classes`, and `state_high_cost_rules` are the volatile
/// registries the engine resolves per evaluation.
public interface IAtrQmRepository
{
    /// Insert one append-only result row.
    Task<AtrResultRecord> AddResultAsync(string appId, string? evalId,
        decimal? dti, decimal? apr, decimal? apor, string verdictsJson,
        string actor, CancellationToken ct);

    Task<IReadOnlyList<AtrResultRecord>> ListResultsAsync(
        string appId, CancellationToken ct);

    /// Latest effective APOR row (effective_date ≤ today, newest first);
    /// null when the table is empty — the battery warns, never guesses.
    Task<AporRateRecord?> GetAporAsync(CancellationToken ct);

    /// `pf_classes` registry as a fee_code→category map.
    Task<IReadOnlyDictionary<string, PfClassRecord>> GetPfClassesAsync(
        CancellationToken ct);

    /// The `state_high_cost_rules` row for a state, or null (no rule).
    Task<StateHighCostRecord?> GetStateRuleAsync(
        string stateCode, CancellationToken ct);
}

/// One `atr_results` row.
public sealed record AtrResultRecord(
    string ResultId, string ApplicationId, string? EvalId,
    decimal? Dti, decimal? Apr, decimal? Apor, string VerdictsJson,
    string Actor, string? CreatedUtc);

/// One `apor_rates` row.
public sealed record AporRateRecord(string EffectiveDate, decimal Apor);

/// One `pf_classes` registry row.
public sealed record PfClassRecord(string FeeCode, string Category,
    string? Notes);

/// One `state_high_cost_rules` row.
public sealed record StateHighCostRecord(string StateCode,
    decimal RateSpread, decimal PfPct, decimal PfFloor, string? Notes);

/// GET …/atr report — latest evaluation's per-test verdicts + figures,
/// plus the lineage of prior evaluations.
public sealed record AtrQmReportDto(
    AtrEvaluationDto? Latest,
    IReadOnlyList<AtrEvaluationDto> History);

/// One evaluation as the wire sees it — verdicts parsed out of the
/// stored JSON, engine figures echoed.
public sealed record AtrEvaluationDto(
    string ResultId, string? EvalId, decimal? Dti, decimal? Apr,
    decimal? Apor, IReadOnlyList<AtrVerdictDto> Verdicts,
    string Actor, string? CreatedUtc);

/// One test verdict — test name, pass/warn/fail, human detail.
public sealed record AtrVerdictDto(string Test, string Verdict,
    string Detail);
