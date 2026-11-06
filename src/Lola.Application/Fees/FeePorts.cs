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
/// One scenario variant in a compare batch (MPE-188) — typed
/// overrides merged over the document's base scenario. `Label`
/// tags the variant for display; the tag (label ?? loanType ?? "")
/// is the override/merge key, so a batch can't hold two variants
/// that resolve to the same tag.
public sealed record FeeVariant(
    string? Label, string? LoanType, bool? DiscountPoints);

public sealed record FeeRequestOptions(
    IReadOnlyList<string>? FeeCodes = null,
    IReadOnlyList<FeeVariant>? Variants = null);

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

/// One persisted evaluation (tenant-file `fee_evals`, MPE-183).
/// `Status`: pending → done | failed. Rows are never deleted — the
/// table IS the history (GET …/fees/history reads it directly).
public sealed record FeeEvalRecord(
    string EvalId, string ApplicationId, string Adapter,
    string RequestHash, string ScenarioJson, string Status,
    string? ResultJson, string? Error, string Actor,
    string? CreatedUtc, string? CompletedUtc,
    string? BatchId = null, string Variant = "");

/// One queued outbound eval (tenant-file `fee_outbox`). `Status`:
/// pending → done | dlq.
public sealed record FeeOutboxRow(
    string OutboxId, string Kind, string EvalId, string RequestHash,
    string PayloadJson, string Status, long Attempts,
    string? NextRetryUtc, string? LastError);

/// One fee line of a completed eval (tenant-file `fee_items`,
/// MPE-183) — the stable identity Phase 3 overrides key off.
public sealed record FeeItemRecord(
    string ItemId, string EvalId, string FeeCode, string? Label,
    string Section, double Base, string AdjustmentsJson,
    double Total, string? CreatedUtc);

/// Enqueue outcome — a duplicated request returns the ORIGINAL eval
/// id with `Deduplicated = true` instead of inserting (idempotency).
public sealed record FeeEnqueueResult(string EvalId, bool Deduplicated);

/// Wire-facing eval DTO — the normalized adapter result rides along
/// when the eval has completed.
public sealed record FeeEvalDto(
    string EvalId, string ApplicationId, string Adapter, string Status,
    FeeEvaluation? Result, string? Error, string Actor,
    string? CreatedUtc, string? CompletedUtc,
    string? BatchId = null, string Variant = "");

/// Fee persistence seam (MPE-183). The DataPlane implementation owns
/// the transaction discipline; handlers and the outbox worker only
/// see this surface.
public partial interface IFeeRepository
{
    /// Insert eval + outbox row in ONE transaction. `request_hash`
    /// UNIQUE is the idempotency gate: a duplicate returns the
    /// existing eval id with Deduplicated=true and inserts nothing.
    Task<FeeEnqueueResult> EnqueueAsync(FeeEvalRecord eval,
        string outboxId, string kind, string payloadJson,
        CancellationToken ct = default);

    Task<FeeEvalRecord?> GetEvalAsync(string evalId,
        CancellationToken ct = default);
    Task<FeeEvalRecord?> GetLatestEvalAsync(string appId,
        CancellationToken ct = default);
    Task<IReadOnlyList<FeeEvalRecord>> ListEvalsAsync(string appId,
        CancellationToken ct = default);

    /// Due pending outbox rows (next_retry_utc null or ≤ nowUtc).
    Task<IReadOnlyList<FeeOutboxRow>> ListDueOutboxAsync(
        string nowUtc, int limit, CancellationToken ct = default);

    /// Worker success — outbox done + eval done/result + fee_items
    /// rows in ONE tx.
    Task CompleteAsync(string outboxId, string evalId, string adapter,
        string resultJson, IReadOnlyList<FeeItemRecord> items,
        string completedUtc, CancellationToken ct = default);

    /// Transient failure — bump attempts, schedule next retry.
    Task RescheduleAsync(string outboxId, string evalId, long attempts,
        string nextRetryUtc, string error, CancellationToken ct = default);

    /// Terminal failure — outbox dlq + eval failed.
    Task DeadLetterAsync(string outboxId, string evalId, long attempts,
        string error, CancellationToken ct = default);
}

/// One fee override (tenant-file `fee_overrides`, MPE-184) — an
/// application-level fact keyed by (app, feeCode, section), so it
/// survives re-evaluation. `Stale` marks orphans whose fee vanished
/// from the latest eval — surfaced, never deleted.
public sealed record FeeOverrideRecord(
    string OverrideId, string ApplicationId, string FeeCode,
    string Section, double Amount, string Reason, string Actor,
    string? CreatedUtc, long Stale, string? ClearedUtc,
    string Variant = "");

/// A fee_items row resolved with its owning application id.
public sealed record FeeItemLookup(
    FeeItemRecord Item, string ApplicationId, string Variant);

/// Wire-facing line: computed from the eval, effective = override
/// when one is set.
public sealed record FeeLineDto(
    string ItemId, string FeeCode, string? Label, decimal Base,
    decimal Computed, decimal? OverrideAmount, string? OverrideReason,
    decimal Effective);

/// LE cost-block section with its lines and totals.
public sealed record FeeSectionDto(
    string Section, IReadOnlyList<FeeLineDto> Items,
    decimal ComputedTotal, decimal EffectiveTotal);

/// An orphaned override — the fee vanished from the latest eval but
/// the override survives, marked stale (MPE-184).
public sealed record StaleOverrideDto(
    string OverrideId, string FeeCode, string Section,
    decimal Amount, string Reason, string Actor, string? CreatedUtc,
    string Variant = "");

/// GET …/fees response (MPE-188): the latest batch's breakdowns —
/// one per requested variant, so FHA and conventional sit
/// side-by-side.
public sealed record FeeComparisonDto(
    string BatchId, IReadOnlyList<FeeBreakdownDto> Evals);

/// GET …/fees response (MPE-184): section-grouped lines with
/// effective totals, plus the stale-override surface.
public sealed record FeeBreakdownDto(
    string EvalId, string? Variant, string ApplicationId,
    string Adapter, string Status,
    string? Error, IReadOnlyList<FeeSectionDto> Sections,
    decimal ComputedTotal, decimal EffectiveTotal,
    IReadOnlyList<StaleOverrideDto> StaleOverrides,
    string? CreatedUtc, string? CompletedUtc);

public partial interface IFeeRepository
{
    /// A fee_items row by id, joined to its owning application.
    Task<FeeItemLookup?> GetItemAsync(string itemId,
        CancellationToken ct = default);

    /// All line items for one eval.
    Task<IReadOnlyList<FeeItemRecord>> ListItemsAsync(string evalId,
        CancellationToken ct = default);

    /// An override row by id.
    Task<FeeOverrideRecord?> GetOverrideAsync(string overrideId,
        CancellationToken ct = default);

    /// Insert or replace the override for (app, feeCode, section) —
    /// re-setting clears Stale and ClearedUtc.
    Task<FeeOverrideRecord> UpsertOverrideAsync(
        FeeOverrideRecord o, CancellationToken ct = default);

    /// Mark an override cleared (row stays — cleared_utc stamps it;
    /// it stops affecting effective totals).
    Task ClearOverrideAsync(string overrideId, string clearedUtc,
        CancellationToken ct = default);

    /// All uncleared overrides for an application.
    Task<IReadOnlyList<FeeOverrideRecord>> ListOverridesAsync(
        string appId, CancellationToken ct = default);

    /// The most recent batch's evals — every row sharing the newest
    /// eval's batch_id (a single-eval request batches to itself via
    /// COALESCE(batch_id, eval_id)).
    Task<IReadOnlyList<FeeEvalRecord>> GetLatestBatchEvalsAsync(
        string appId, CancellationToken ct = default);
}
