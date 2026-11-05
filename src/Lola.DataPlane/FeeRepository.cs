using Dapper;
using Lola.Application.Fees;
using Microsoft.Data.Sqlite;

namespace Lola.DataPlane;

/// Tenant-file fee store (MPE-183): `fee_evals` history +
/// `fee_outbox` work queue + `fee_items` line store. The
/// multi-mutation methods hold the transaction discipline the
/// ticket requires — enqueue is eval+row atomically, completion is
/// outbox+eval+items atomically.
public sealed class FeeRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IFeeRepository
{
    public async Task<FeeEnqueueResult> EnqueueAsync(FeeEvalRecord eval,
        string outboxId, string kind, string payloadJson,
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
                "SELECT eval_id FROM fee_outbox WHERE request_hash=@hash",
                new { hash = eval.RequestHash }, tx);
            if (existing is not null)
            {
                await tx.CommitAsync(ct);
                return new FeeEnqueueResult(existing, true);
            }

            await conn.ExecuteAsync(
                """
                INSERT INTO fee_evals
                    (eval_id, application_id, adapter, request_hash,
                     scenario_json, status, actor)
                VALUES (@evalId, @appId, @adapter, @hash,
                        @scenario, 'pending', @actor)
                """,
                new
                {
                    evalId = eval.EvalId,
                    appId = eval.ApplicationId,
                    adapter = eval.Adapter,
                    hash = eval.RequestHash,
                    scenario = eval.ScenarioJson,
                    actor = eval.Actor
                }, tx);
            await conn.ExecuteAsync(
                """
                INSERT INTO fee_outbox
                    (outbox_id, kind, eval_id, request_hash, payload_json)
                VALUES (@outboxId, @kind, @evalId, @hash, @payload)
                """,
                new
                {
                    outboxId,
                    kind,
                    evalId = eval.EvalId,
                    hash = eval.RequestHash,
                    payload = payloadJson
                }, tx);
            await tx.CommitAsync(ct);
            return new FeeEnqueueResult(eval.EvalId, false);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private const string EvalColumns = """
        eval_id AS EvalId, application_id AS ApplicationId,
        adapter AS Adapter, request_hash AS RequestHash,
        scenario_json AS ScenarioJson, status AS Status,
        result_json AS ResultJson, error AS Error, actor AS Actor,
        created_utc AS CreatedUtc, completed_utc AS CompletedUtc
        """;

    public async Task<FeeEvalRecord?> GetEvalAsync(string evalId,
        CancellationToken ct = default)
        => await QuerySingleOrDefaultAsync<FeeEvalRecord>(
            $"SELECT {EvalColumns} FROM fee_evals WHERE eval_id=@evalId",
            new { evalId }, ct);

    public async Task<FeeEvalRecord?> GetLatestEvalAsync(string appId,
        CancellationToken ct = default)
        => await QuerySingleOrDefaultAsync<FeeEvalRecord>(
            $"""
            SELECT {EvalColumns} FROM fee_evals
            WHERE application_id=@appId
            ORDER BY created_utc DESC LIMIT 1
            """, new { appId }, ct);

    public async Task<IReadOnlyList<FeeEvalRecord>> ListEvalsAsync(
        string appId, CancellationToken ct = default)
        => await QueryAsync<FeeEvalRecord>(
            $"""
            SELECT {EvalColumns} FROM fee_evals
            WHERE application_id=@appId ORDER BY created_utc
            """, new { appId }, ct);

    public async Task<IReadOnlyList<FeeOutboxRow>> ListDueOutboxAsync(
        string nowUtc, int limit, CancellationToken ct = default)
        => await QueryAsync<FeeOutboxRow>(
            """
            SELECT outbox_id AS OutboxId, kind AS Kind, eval_id AS EvalId,
                   request_hash AS RequestHash, payload_json AS PayloadJson,
                   status AS Status, attempts AS Attempts,
                   next_retry_utc AS NextRetryUtc, last_error AS LastError
            FROM fee_outbox
            WHERE status='pending'
              AND (next_retry_utc IS NULL OR next_retry_utc <= @nowUtc)
            ORDER BY created_utc LIMIT @limit
            """, new { nowUtc, limit }, ct);

    public async Task CompleteAsync(string outboxId, string evalId,
        string adapter, string resultJson,
        IReadOnlyList<FeeItemRecord> items, string completedUtc,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            await conn.ExecuteAsync(
                """
                UPDATE fee_outbox
                SET status='done', updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE outbox_id=@outboxId
                """, new { outboxId }, tx);
            await conn.ExecuteAsync(
                """
                UPDATE fee_evals
                SET status='done', adapter=@adapter, result_json=@resultJson,
                    completed_utc=@completedUtc
                WHERE eval_id=@evalId
                """, new { evalId, adapter, resultJson, completedUtc }, tx);
            foreach (var item in items)
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO fee_items
                        (item_id, eval_id, fee_code, label, section, base,
                         adjustments_json, total)
                    VALUES (@itemId, @evalId, @feeCode, @label, @section,
                            @baseAmount, @adjustments, @total)
                    """,
                    new
                    {
                        itemId = item.ItemId,
                        evalId = item.EvalId,
                        feeCode = item.FeeCode,
                        label = item.Label,
                        section = item.Section,
                        baseAmount = item.Base,
                        adjustments = item.AdjustmentsJson,
                        total = item.Total
                    }, tx);
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task RescheduleAsync(string outboxId, string evalId,
        long attempts, string nextRetryUtc, string error,
        CancellationToken ct = default)
        => await ExecuteAsync(
            """
            UPDATE fee_outbox
            SET attempts=@attempts, next_retry_utc=@nextRetryUtc,
                last_error=@error,
                updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE outbox_id=@outboxId
            """, new { outboxId, attempts, nextRetryUtc, error }, ct);

    public async Task DeadLetterAsync(string outboxId, string evalId,
        long attempts, string error, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            await conn.ExecuteAsync(
                """
                UPDATE fee_outbox
                SET status='dlq', attempts=@attempts, last_error=@error,
                    updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE outbox_id=@outboxId
                """, new { outboxId, attempts, error }, tx);
            await conn.ExecuteAsync(
                """
                UPDATE fee_evals
                SET status='failed', error=@error, completed_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE eval_id=@evalId
                """, new { evalId, error }, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
