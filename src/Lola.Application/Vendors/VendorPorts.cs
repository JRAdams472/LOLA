namespace Lola.Application.Vendors;

/// Tenant-file vendor store port — `vendor_orders` registry +
/// `vendor_outbox` work queue (same shape as the fee/doc/vault
/// outboxes). Implementations live in the data plane; handlers and
/// the drain processor see only this surface.
public interface IVendorOrderRepository
{
    /// Insert order + outbox row atomically. Returns the existing row
    /// when `request_hash` repeats — order placement is idempotent.
    Task<VendorEnqueueResult> EnqueueAsync(VendorOrderRow order,
        string outboxId, CancellationToken ct = default);

    Task<VendorOrderRow?> GetAsync(string orderId,
        CancellationToken ct = default);
    /// All orders carrying this external id. Normally ≤1; vendors may
    /// dedupe identical submissions onto one tracking id, so callers
    /// must handle multiple matches (CompleteVendorOrderHandler picks
    /// the single non-terminal match, else Ambiguous).
    Task<IReadOnlyList<VendorOrderRow>> ListByExternalIdAsync(
        string kind, string externalId, CancellationToken ct = default);
    Task<IReadOnlyList<VendorOrderRow>> ListByApplicationAsync(
        string applicationId, CancellationToken ct = default);

    /// Receipt landed — order moves ordered → submitted. `pollAfterUtc`
    /// non-null keeps the outbox row pending as the poll handle
    /// (`next_retry_utc`, attempts reset); null closes it — push
    /// vendors complete exclusively via webhook.
    Task MarkSubmittedAsync(string orderId, string externalId,
        string outboxId, string? pollAfterUtc,
        CancellationToken ct = default);

    /// The outbox row paired with an order — the webhook channel
    /// needs it to close the poll handle atomically on completion.
    Task<VendorOutboxRow?> FindOutboxByOrderAsync(string orderId,
        CancellationToken ct = default);

    /// Completion is order row + outbox row + step event, atomically —
    /// `resultJson` arrives already `vault:`-ciphertext.
    Task CompleteAsync(string orderId, string appId,
        string resultJson, string outboxId, string outcome,
        string step, string actor, CancellationToken ct = default);
    /// Terminal failure — order→failed, pending outbox→dlq, and the
    /// step event in one transaction.
    Task FailAsync(string orderId, string appId, string error,
        string outboxId, long attempts, string step, string actor,
        CancellationToken ct = default);

    Task<IReadOnlyList<VendorOutboxRow>> ListDueOutboxAsync(
        string nowUtc, int limit, CancellationToken ct = default);
    /// Retryable failure — bump attempts, schedule `nextRetryUtc`.
    Task MarkOutboxRetryAsync(string outboxId, long attempts,
        string nextRetryUtc, string? error, CancellationToken ct = default);
    Task MarkOutboxDlqAsync(string outboxId, long attempts, string error,
        CancellationToken ct = default);
}

public sealed record VendorEnqueueResult(string OrderId, bool Existing);

/// `vendor_orders` row — `RequestJson`/`ResultJson` are `vault:`
/// ciphertext; every other column is PII-free metadata.
public sealed record VendorOrderRow(
    string OrderId, string ApplicationId, string Kind, string Adapter,
    string? ExternalId, string Status, string RequestJson,
    string? ResultJson, string? Error, string OrderedBy,
    string? CreatedUtc, string? SubmittedUtc, string? CompletedUtc,
    string RequestHash);

public sealed record VendorOutboxRow(
    string OutboxId, string OrderId, string RequestHash,
    string Status, long Attempts, string? NextRetryUtc,
    string? LastError);

/// Resolves the CURRENT tenant's credential material for one vendor
/// kind. Implementation reads OpenBao KV (`lola-kv/vendors/{tenant}/
/// {kind}`) — never config, never a DB row, never the repo. Returns
/// null when the tenant has no binding for the kind; the adapter
/// turns that into a typed auth failure.
public interface IVendorCredentialSource
{
    Task<VendorCredentials?> ForAsync(VendorKind kind,
        CancellationToken ct = default);
}
