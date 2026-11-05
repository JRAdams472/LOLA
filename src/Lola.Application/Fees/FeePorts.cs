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

/// One persisted evaluation (tenant-file `fee_evals`, MPE-183).
/// `Status`: pending → done | failed. Rows are never deleted — the
/// table IS the history (GET …/fees/history reads it directly).
public sealed record FeeEvalRecord(
    string EvalId, string ApplicationId, string Adapter,
    string RequestHash, string ScenarioJson, string Status,
    string? ResultJson, string? Error, string Actor,
    string? CreatedUtc, string? CompletedUtc);

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
    string Section, decimal Base, string AdjustmentsJson,
    decimal Total, string? CreatedUtc);

/// Enqueue outcome — a duplicated request returns the ORIGINAL eval
/// id with `Deduplicated = true` instead of inserting (idempotency).
public sealed record FeeEnqueueResult(string EvalId, bool Deduplicated);

/// Wire-facing eval DTO — the normalized adapter result rides along
/// when the eval has completed.
public sealed record FeeEvalDto(
    string EvalId, string ApplicationId, string Adapter, string Status,
    FeeEvaluation? Result, string? Error, string Actor,
    string? CreatedUtc, string? CompletedUtc);

/// Fee persistence seam (MPE-183). The DataPlane implementation owns
/// the transaction discipline; handlers and the outbox worker only
/// see this surface.
public interface IFeeRepository
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
