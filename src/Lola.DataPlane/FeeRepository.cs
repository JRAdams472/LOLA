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
            // Re-eval merge (MPE-184): overrides keyed (app, fee_code,
            // section) that match a new item go live; orphans — fees
            // that vanished from this eval — mark stale, never drop.
            await conn.ExecuteAsync(
                """
                UPDATE fee_overrides
                SET stale = CASE WHEN (fee_code, section) IN
                        (SELECT fee_code, section FROM fee_items
                         WHERE eval_id=@evalId) THEN 0 ELSE 1 END
                WHERE cleared_utc IS NULL
                  AND application_id = (SELECT application_id
                        FROM fee_evals WHERE eval_id=@evalId)
                """, new { evalId }, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private const string OverrideColumns = """
        override_id AS OverrideId, application_id AS ApplicationId,
        fee_code AS FeeCode, section AS Section, amount AS Amount,
        reason AS Reason, actor AS Actor, created_utc AS CreatedUtc,
        stale AS Stale, cleared_utc AS ClearedUtc
        """;

    public async Task<FeeItemLookup?> GetItemAsync(string itemId,
        CancellationToken ct = default)
    {
        var row = await QuerySingleOrDefaultAsync<ItemJoin>(
            """
            SELECT i.item_id AS ItemId, i.eval_id AS EvalId,
                   i.fee_code AS FeeCode, i.label AS Label,
                   i.section AS Section, i.base AS Base,
                   i.adjustments_json AS AdjustmentsJson,
                   i.total AS Total, i.created_utc AS CreatedUtc,
                   e.application_id AS ApplicationId
            FROM fee_items i JOIN fee_evals e ON e.eval_id=i.eval_id
            WHERE i.item_id=@itemId
            """, new { itemId }, ct);
        return row is null ? null : new FeeItemLookup(
            new FeeItemRecord(row.ItemId, row.EvalId, row.FeeCode,
                row.Label, row.Section, row.Base, row.AdjustmentsJson,
                row.Total, row.CreatedUtc), row.ApplicationId);
    }
    private sealed record ItemJoin(
        string ItemId, string EvalId, string FeeCode, string? Label,
        string Section, double Base, string AdjustmentsJson,
        double Total, string? CreatedUtc, string ApplicationId);

    public async Task<IReadOnlyList<FeeItemRecord>> ListItemsAsync(
        string evalId, CancellationToken ct = default)
        => await QueryAsync<FeeItemRecord>(
            """
            SELECT item_id AS ItemId, eval_id AS EvalId,
                   fee_code AS FeeCode, label AS Label,
                   section AS Section, base AS Base,
                   adjustments_json AS AdjustmentsJson,
                   total AS Total, created_utc AS CreatedUtc
            FROM fee_items WHERE eval_id=@evalId ORDER BY item_id
            """, new { evalId }, ct);

    public async Task<FeeOverrideRecord?> GetOverrideAsync(
        string overrideId, CancellationToken ct = default)
        => await QuerySingleOrDefaultAsync<FeeOverrideRecord>(
            $"SELECT {OverrideColumns} FROM fee_overrides " +
            "WHERE override_id=@overrideId", new { overrideId }, ct);

    public async Task<FeeOverrideRecord> UpsertOverrideAsync(
        FeeOverrideRecord o, CancellationToken ct = default)
    {
        await ExecuteAsync(
            """
            INSERT INTO fee_overrides
                (override_id, application_id, fee_code, section,
                 amount, reason, actor, created_utc, stale)
            VALUES (@overrideId, @appId, @feeCode, @section,
                    @amount, @reason, @actor, @utc, 0)
            ON CONFLICT (application_id, fee_code, section)
            DO UPDATE SET amount=@amount, reason=@reason, actor=@actor,
                created_utc=@utc, stale=0, cleared_utc=NULL
            """,
            new
            {
                overrideId = o.OverrideId,
                appId = o.ApplicationId,
                feeCode = o.FeeCode,
                section = o.Section,
                amount = o.Amount,
                reason = o.Reason,
                actor = o.Actor,
                utc = o.CreatedUtc
            }, ct);
        // On conflict the id stays with the EXISTING row — read back
        // the winner rather than trusting ours.
        return (await QuerySingleOrDefaultAsync<FeeOverrideRecord>(
            $"SELECT {OverrideColumns} FROM fee_overrides " +
            "WHERE application_id=@appId AND fee_code=@feeCode " +
            "AND section=@section AND cleared_utc IS NULL",
            new
            {
                appId = o.ApplicationId,
                feeCode = o.FeeCode,
                section = o.Section
            }, ct))!;
    }

    public async Task ClearOverrideAsync(string overrideId,
        string clearedUtc, CancellationToken ct = default)
        => await ExecuteAsync(
            """
            UPDATE fee_overrides SET cleared_utc=@clearedUtc
            WHERE override_id=@overrideId AND cleared_utc IS NULL
            """, new { overrideId, clearedUtc }, ct);

    public async Task<IReadOnlyList<FeeOverrideRecord>>
        ListOverridesAsync(string appId, CancellationToken ct = default)
        => await QueryAsync<FeeOverrideRecord>(
            $"SELECT {OverrideColumns} FROM fee_overrides " +
            "WHERE application_id=@appId AND cleared_utc IS NULL " +
            "ORDER BY created_utc", new { appId }, ct);

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
