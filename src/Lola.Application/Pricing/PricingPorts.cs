namespace Lola.Application.Pricing;

/// One persisted evaluation (tenant-file `pricing_evals`, MPE-130).
/// `Status`: pending → done | failed. Rows are never deleted — the
/// table IS the history (GET …/pricing/history reads it directly).
public sealed record PricingEvalRecord(
    string EvalId, string ApplicationId, string Adapter, string? AsOf,
    string RequestHash, string ScenarioJson, string Status,
    string? ResultJson, string? Error, string Actor,
    string? CreatedUtc, string? CompletedUtc);

/// One queued outbound eval (tenant-file `pricing_outbox`). `Kind`
/// distinguishes a fresh eval from a snapshot re-eval (MPE-132 wires
/// `reval`); `Status`: pending → done | dlq.
public sealed record PricingOutboxRow(
    string OutboxId, string Kind, string EvalId, string RequestHash,
    string PayloadJson, string Status, long Attempts,
    string? NextRetryUtc, string? LastError);

/// Enqueue outcome — a duplicated request returns the ORIGINAL eval
/// id with `Deduplicated = true` instead of inserting (idempotency).
public sealed record EnqueueResult(string EvalId, bool Deduplicated);

/// Wire-facing eval DTO — the normalized adapter result rides along
/// when the eval has completed.
public sealed record PricingEvalDto(
    string EvalId, string ApplicationId, string Adapter, string? AsOf,
    string Status, PricingEvaluation? Result, string? Error,
    string Actor, string? CreatedUtc, string? CompletedUtc);

/// Pricing persistence seam (MPE-130). The DataPlane implementation
/// owns the two-table transaction discipline; handlers and the outbox
/// worker only see this surface.
public interface IPricingRepository
{
    /// Insert eval + outbox row in ONE transaction. `request_hash`
    /// UNIQUE is the idempotency gate: a duplicate returns the
    /// existing eval id with Deduplicated=true and inserts nothing.
    Task<EnqueueResult> EnqueueAsync(PricingEvalRecord eval,
        string outboxId, string kind, string payloadJson,
        CancellationToken ct = default);

    Task<PricingEvalRecord?> GetEvalAsync(string evalId,
        CancellationToken ct = default);
    Task<PricingEvalRecord?> GetLatestEvalAsync(string appId,
        CancellationToken ct = default);
    Task<IReadOnlyList<PricingEvalRecord>> ListEvalsAsync(string appId,
        CancellationToken ct = default);

    /// Due pending outbox rows (next_retry_utc null or ≤ nowUtc).
    Task<IReadOnlyList<PricingOutboxRow>> ListDueOutboxAsync(
        string nowUtc, int limit, CancellationToken ct = default);

    /// Worker success — outbox done + eval done/result in ONE tx.
    Task CompleteAsync(string outboxId, string evalId, string adapter,
        string resultJson, string completedUtc, CancellationToken ct = default);

    /// Transient failure — bump attempts, schedule next retry.
    Task RescheduleAsync(string outboxId, string evalId, long attempts,
        string nextRetryUtc, string error, CancellationToken ct = default);

    /// Terminal failure — outbox dlq + eval failed.
    Task DeadLetterAsync(string outboxId, string evalId, long attempts,
        string error, CancellationToken ct = default);
}
