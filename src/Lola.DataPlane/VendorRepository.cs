using Dapper;
using Lola.Application.Vendors;
using Microsoft.Data.Sqlite;

namespace Lola.DataPlane;

/// Tenant-file vendor store (MPE-226): `vendor_orders` registry +
/// `vendor_outbox` work queue. The multi-mutation methods hold the
/// transaction discipline the seam requires — enqueue is order+outbox
/// atomically, completion is order+outbox atomically.
public sealed class VendorRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IVendorOrderRepository
{
    private const string OrderColumns =
        "order_id AS OrderId, application_id AS ApplicationId, " +
        "kind AS Kind, adapter AS Adapter, external_id AS ExternalId, " +
        "status AS Status, request_json AS RequestJson, " +
        "result_json AS ResultJson, error AS Error, " +
        "ordered_by AS OrderedBy, created_utc AS CreatedUtc, " +
        "submitted_utc AS SubmittedUtc, " +
        "completed_utc AS CompletedUtc, request_hash AS RequestHash";

    public async Task<VendorEnqueueResult> EnqueueAsync(
        VendorOrderRow order, string outboxId,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        // Serializable: the hash check + two inserts must not race a
        // duplicate enqueue — SQLite serializes writers, so the
        // UNIQUE(request_hash) is the belt to this suspenders.
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            var existing = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT order_id FROM vendor_outbox WHERE request_hash=@hash",
                new { hash = order.RequestHash }, tx);
            if (existing is not null)
            {
                await tx.CommitAsync(ct);
                return new VendorEnqueueResult(existing, true);
            }

            await conn.ExecuteAsync(
                """
                INSERT INTO vendor_orders
                    (order_id, application_id, kind, adapter, status,
                     request_json, ordered_by, request_hash)
                VALUES (@orderId, @appId, @kind, @adapter, 'ordered',
                        @request, @actor, @hash)
                """,
                new
                {
                    orderId = order.OrderId,
                    appId = order.ApplicationId,
                    kind = order.Kind,
                    adapter = order.Adapter,
                    request = order.RequestJson,
                    actor = order.OrderedBy,
                    hash = order.RequestHash
                }, tx);
            await conn.ExecuteAsync(
                """
                INSERT INTO vendor_outbox (outbox_id, order_id, request_hash)
                VALUES (@outboxId, @orderId, @hash)
                """,
                new
                {
                    outboxId,
                    orderId = order.OrderId,
                    hash = order.RequestHash
                }, tx);
            await tx.CommitAsync(ct);
            return new VendorEnqueueResult(order.OrderId, false);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public Task<VendorOrderRow?> GetAsync(string orderId,
        CancellationToken ct = default)
        => QuerySingleOrDefaultAsync<VendorOrderRow>(
            $"SELECT {OrderColumns} FROM vendor_orders WHERE order_id=@id",
            new { id = orderId }, ct);

    public Task<VendorOrderRow?> FindByExternalIdAsync(string kind,
        string externalId, CancellationToken ct = default)
        => QuerySingleOrDefaultAsync<VendorOrderRow>(
            $"SELECT {OrderColumns} FROM vendor_orders " +
            "WHERE kind=@kind AND external_id=@ext",
            new { kind, ext = externalId }, ct);

    public Task<IReadOnlyList<VendorOrderRow>> ListByApplicationAsync(
        string applicationId, CancellationToken ct = default)
        => QueryAsync<VendorOrderRow>(
            $"SELECT {OrderColumns} FROM vendor_orders " +
            "WHERE application_id=@appId ORDER BY created_utc",
            new { appId = applicationId }, ct);

    public async Task MarkSubmittedAsync(string orderId,
        string externalId, string outboxId, string? pollAfterUtc,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            await conn.ExecuteAsync(
                """
                UPDATE vendor_orders
                SET status='submitted', external_id=@ext,
                    submitted_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE order_id=@id
                """, new { id = orderId, ext = externalId }, tx);
            // Poll vendors keep the row pending — it doubles as the
            // poll handle (next_retry_utc schedule, attempts reset);
            // push vendors close it and complete via webhook.
            if (pollAfterUtc is not null)
                await conn.ExecuteAsync(
                    "UPDATE vendor_outbox SET attempts=0, " +
                    "next_retry_utc=@next, " +
                    "updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') " +
                    "WHERE outbox_id=@id",
                    new { id = outboxId, next = pollAfterUtc }, tx);
            else
                await conn.ExecuteAsync(
                    "UPDATE vendor_outbox SET status='done', " +
                    "updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') " +
                    "WHERE outbox_id=@id",
                    new { id = outboxId }, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public Task<VendorOutboxRow?> FindOutboxByOrderAsync(string orderId,
        CancellationToken ct = default)
        => QuerySingleOrDefaultAsync<VendorOutboxRow>(
            """
            SELECT outbox_id AS OutboxId, order_id AS OrderId,
                   request_hash AS RequestHash, status AS Status,
                   attempts AS Attempts, next_retry_utc AS NextRetryUtc,
                   last_error AS LastError
            FROM vendor_outbox WHERE order_id=@id
            """, new { id = orderId }, ct);

    public async Task CompleteAsync(string orderId, string appId,
        string resultJson, string outboxId, string outcome,
        string step, string actor, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            var changed = await conn.ExecuteAsync(
                """
                UPDATE vendor_orders
                SET status=@outcome, result_json=@result,
                    completed_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE order_id=@id AND status IN ('ordered','submitted')
                """,
                new { id = orderId, outcome, result = resultJson }, tx);
            if (changed > 0)
                await conn.ExecuteAsync(
                    """
                    INSERT INTO application_step_events
                        (id, application_id, step, event, actor, reason)
                    VALUES (@eventId, @appId, @step, @outcome, @actor,
                            @reason)
                    """,
                    new
                    {
                        eventId = $"{appId}-step-{Guid.NewGuid():N}",
                        appId,
                        step,
                        outcome,
                        actor,
                        reason = "vendor order completed"
                    }, tx);
            await conn.ExecuteAsync(
                "UPDATE vendor_outbox SET status='done', " +
                "updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now') " +
                "WHERE outbox_id=@id AND status='pending'",
                new { id = outboxId }, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task FailAsync(string orderId, string appId,
        string error, string outboxId, long attempts, string step,
        string actor, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            var changed = await conn.ExecuteAsync(
                """
                UPDATE vendor_orders
                SET status='failed', error=@error,
                    completed_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE order_id=@id AND status IN ('ordered','submitted')
                """,
                new { id = orderId, error }, tx);
            if (changed > 0)
                await conn.ExecuteAsync(
                    """
                    INSERT INTO application_step_events
                        (id, application_id, step, event, actor, reason)
                    VALUES (@eventId, @appId, @step, 'failed', @actor,
                            @reason)
                    """,
                    new
                    {
                        eventId = $"{appId}-step-{Guid.NewGuid():N}",
                        appId,
                        step,
                        actor,
                        reason = "vendor order failed"
                    }, tx);
            // 'pending' → 'dlq'; an already-closed row (webhook-driven
            // failure after submit) is left as-is.
            await conn.ExecuteAsync(
                """
                UPDATE vendor_outbox
                SET status='dlq', attempts=@attempts, last_error=@error,
                    updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE outbox_id=@id AND status='pending'
                """,
                new { id = outboxId, attempts, error }, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public Task<IReadOnlyList<VendorOutboxRow>> ListDueOutboxAsync(
        string nowUtc, int limit, CancellationToken ct = default)
        => QueryAsync<VendorOutboxRow>(
            """
            SELECT outbox_id AS OutboxId, order_id AS OrderId,
                   request_hash AS RequestHash, status AS Status,
                   attempts AS Attempts, next_retry_utc AS NextRetryUtc,
                   last_error AS LastError
            FROM vendor_outbox
            WHERE status='pending'
              AND (next_retry_utc IS NULL OR next_retry_utc<=@now)
            ORDER BY created_utc LIMIT @limit
            """,
            new { now = nowUtc, limit }, ct);

    public Task MarkOutboxRetryAsync(string outboxId, long attempts,
        string nextRetryUtc, string? error, CancellationToken ct = default)
        => ExecuteAsync(
            """
            UPDATE vendor_outbox
            SET attempts=@attempts, next_retry_utc=@retry,
                last_error=@error,
                updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE outbox_id=@id
            """,
            new { id = outboxId, attempts, retry = nextRetryUtc, error }, ct);

    public Task MarkOutboxDlqAsync(string outboxId, long attempts,
        string error, CancellationToken ct = default)
        => ExecuteAsync(
            """
            UPDATE vendor_outbox
            SET status='dlq', attempts=@attempts, last_error=@error,
                updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE outbox_id=@id
            """,
            new { id = outboxId, attempts, error }, ct);
}
