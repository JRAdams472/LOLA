using Dapper;
using Lola.Application.Pricing;
using Microsoft.Data.Sqlite;

namespace Lola.DataPlane;

/// Tenant-file pricing store (MPE-130): `pricing_evals` history +
/// `pricing_outbox` work queue. The two-mutation methods hold the
/// transaction discipline the ticket requires — enqueue is eval+row
/// atomically, completion is outbox+eval atomically.
public sealed class PricingRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IPricingRepository
{
    public async Task<EnqueueResult> EnqueueAsync(PricingEvalRecord eval,
        string outboxId, string kind, string payloadJson,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        // IMMEDIATE: the hash check + two inserts must not race a
        // duplicate enqueue — SQLite serializes writers, so the
        // UNIQUE(request_hash) is the belt to this suspenders.
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            var existing = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT eval_id FROM pricing_outbox WHERE request_hash=@hash",
                new { hash = eval.RequestHash }, tx);
            if (existing is not null)
            {
                await tx.CommitAsync(ct);
                return new EnqueueResult(existing, true);
            }

            await conn.ExecuteAsync(
                """
                INSERT INTO pricing_evals
                    (eval_id, application_id, adapter, as_of, request_hash,
                     scenario_json, status, actor)
                VALUES (@evalId, @appId, @adapter, @asOf, @hash,
                        @scenario, 'pending', @actor)
                """,
                new
                {
                    evalId = eval.EvalId,
                    appId = eval.ApplicationId,
                    adapter = eval.Adapter,
                    asOf = eval.AsOf,
                    hash = eval.RequestHash,
                    scenario = eval.ScenarioJson,
                    actor = eval.Actor
                }, tx);
            await conn.ExecuteAsync(
                """
                INSERT INTO pricing_outbox
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
            return new EnqueueResult(eval.EvalId, false);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private const string EvalColumns = """
        eval_id AS EvalId, application_id AS ApplicationId,
        adapter AS Adapter, as_of AS AsOf, request_hash AS RequestHash,
        scenario_json AS ScenarioJson, status AS Status,
        result_json AS ResultJson, error AS Error, actor AS Actor,
        created_utc AS CreatedUtc, completed_utc AS CompletedUtc
        """;

    public async Task<PricingEvalRecord?> GetEvalAsync(string evalId,
        CancellationToken ct = default)
        => await QuerySingleOrDefaultAsync<PricingEvalRecord>(
            $"SELECT {EvalColumns} FROM pricing_evals WHERE eval_id=@evalId",
            new { evalId }, ct);

    public async Task<PricingEvalRecord?> GetLatestEvalAsync(string appId,
        CancellationToken ct = default)
        => await QuerySingleOrDefaultAsync<PricingEvalRecord>(
            $"""
            SELECT {EvalColumns} FROM pricing_evals
            WHERE application_id=@appId
            ORDER BY created_utc DESC LIMIT 1
            """, new { appId }, ct);

    public async Task<IReadOnlyList<PricingEvalRecord>> ListEvalsAsync(
        string appId, CancellationToken ct = default)
        => await QueryAsync<PricingEvalRecord>(
            $"""
            SELECT {EvalColumns} FROM pricing_evals
            WHERE application_id=@appId ORDER BY created_utc
            """, new { appId }, ct);

    public async Task<IReadOnlyList<PricingOutboxRow>> ListDueOutboxAsync(
        string nowUtc, int limit, CancellationToken ct = default)
        => await QueryAsync<PricingOutboxRow>(
            """
            SELECT outbox_id AS OutboxId, kind AS Kind, eval_id AS EvalId,
                   request_hash AS RequestHash, payload_json AS PayloadJson,
                   status AS Status, attempts AS Attempts,
                   next_retry_utc AS NextRetryUtc, last_error AS LastError
            FROM pricing_outbox
            WHERE status='pending'
              AND (next_retry_utc IS NULL OR next_retry_utc <= @nowUtc)
            ORDER BY created_utc LIMIT @limit
            """, new { nowUtc, limit }, ct);

    public async Task CompleteAsync(string outboxId, string evalId,
        string adapter, string resultJson, string completedUtc,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            await conn.ExecuteAsync(
                """
                UPDATE pricing_outbox
                SET status='done', updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE outbox_id=@outboxId
                """, new { outboxId }, tx);
            await conn.ExecuteAsync(
                """
                UPDATE pricing_evals
                SET status='done', adapter=@adapter, result_json=@resultJson,
                    completed_utc=@completedUtc
                WHERE eval_id=@evalId
                """, new { evalId, adapter, resultJson, completedUtc }, tx);
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
            UPDATE pricing_outbox
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
                UPDATE pricing_outbox
                SET status='dlq', attempts=@attempts, last_error=@error,
                    updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE outbox_id=@outboxId
                """, new { outboxId, attempts, error }, tx);
            await conn.ExecuteAsync(
                """
                UPDATE pricing_evals
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
