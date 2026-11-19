using Dapper;
using Lola.Application.Audit;
using Lola.Application.Events;
using Microsoft.Data.Sqlite;

namespace Lola.DataPlane;

/// Tenant-file mutation event log (M15 P1) — every field change,
/// override, escalation, and lifecycle transition inside this tenant's
/// SQLCipher file lands here. Append-only + hash-chained; a tampered
/// row breaks <see cref="VerifyChainAsync"/> at that seq. Before/after
/// values arrive already encoded by the Application-layer writer
/// (blind-index HMAC for PII classes — never plaintext).
public sealed class LoanEventLog(TenantConnectionFactory factory)
    : TenantRepository(factory), ILoanEventLog
{
    private const string Cols = """
        seq AS Seq, event_id AS EventId, application_id AS ApplicationId,
        event_kind AS EventKind, actor AS Actor, entity AS Entity,
        entity_id AS EntityId, field_path AS FieldPath,
        field_class AS FieldClass, before_val AS BeforeVal,
        after_val AS AfterVal, prev_hash AS PrevHash, hash AS Hash,
        created_utc AS CreatedUtc
        """;

    private sealed class Row
    {
        public long Seq { get; set; }
        public string EventId { get; set; } = "";
        public string ApplicationId { get; set; } = "";
        public string EventKind { get; set; } = "";
        public string Actor { get; set; } = "";
        public string Entity { get; set; } = "";
        public string? EntityId { get; set; }
        public string? FieldPath { get; set; }
        public string? FieldClass { get; set; }
        public string? BeforeVal { get; set; }
        public string? AfterVal { get; set; }
        public string PrevHash { get; set; } = "";
        public string Hash { get; set; } = "";
        public string CreatedUtc { get; set; } = "";

        public LoanEvent ToRecord() => new(Seq, EventId, ApplicationId,
            EventKind, Actor, Entity, EntityId, FieldPath, FieldClass,
            BeforeVal, AfterVal, PrevHash, Hash, CreatedUtc);
    }

    public Task WriteAsync(LoanEventDraft draft,
        CancellationToken ct = default)
        => WriteInternalAsync(draft, null, ct);

    /// Same-tx writer for repositories that own the mutation's
    /// BEGIN IMMEDIATE (e.g. PatchDraftAsync) — the event rows join the
    /// caller's connection so a mutation rollback takes its events with
    /// it. Not on the port: sibling-repo detail, same assembly.
    internal Task WriteOnConnectionAsync(LoanEventDraft draft,
        SqliteConnection conn, CancellationToken ct = default)
        => WriteInternalAsync(draft, conn, ct);

    private async Task WriteInternalAsync(LoanEventDraft draft,
        SqliteConnection? sharedConn, CancellationToken ct)
    {
        var eventId = $"lev-{Guid.NewGuid():N}";
        var createdUtc = DateTimeOffset.UtcNow
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

        if (sharedConn is not null)
        {
            // Caller's BEGIN IMMEDIATE already serializes writers — the
            // last-hash read and insert are atomic inside their tx.
            var prevShared = await LastHashAsync(sharedConn, ct);
            var hashShared = EventChain.ComputeHash(prevShared, eventId,
                draft.ApplicationId, draft.EventKind, draft.Actor,
                draft.Entity, draft.EntityId, draft.FieldPath,
                draft.FieldClass?.ToString(), draft.BeforeVal,
                draft.AfterVal, createdUtc);
            await InsertAsync(sharedConn, eventId, draft, prevShared,
                hashShared, createdUtc, ct);
            return;
        }

        await using var conn = await OpenAsync(ct);
        // Standalone write — own BEGIN IMMEDIATE so the last-hash read
        // and insert can't fork the chain under concurrent writers.
        await conn.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            var prev = await LastHashAsync(conn, ct);
            var hash = EventChain.ComputeHash(prev, eventId,
                draft.ApplicationId, draft.EventKind, draft.Actor,
                draft.Entity, draft.EntityId, draft.FieldPath,
                draft.FieldClass?.ToString(), draft.BeforeVal,
                draft.AfterVal, createdUtc);
            await InsertAsync(conn, eventId, draft, prev, hash,
                createdUtc, ct);
            await conn.ExecuteAsync("COMMIT");
        }
        catch
        {
            await conn.ExecuteAsync("ROLLBACK");
            throw;
        }
    }

    private static async Task<string> LastHashAsync(SqliteConnection conn,
        CancellationToken ct)
        => await conn.QuerySingleOrDefaultAsync<string>(
               new CommandDefinition(
                   "SELECT hash FROM loan_events ORDER BY seq DESC LIMIT 1",
                   cancellationToken: ct))
           ?? HashChain.Genesis;

    private static Task InsertAsync(SqliteConnection conn,
        string eventId, LoanEventDraft d, string prevHash, string hash,
        string createdUtc, CancellationToken ct)
        => conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO loan_events
                (event_id, application_id, event_kind, actor, entity,
                 entity_id, field_path, field_class, before_val,
                 after_val, prev_hash, hash, created_utc)
            VALUES (@eventId, @appId, @kind, @actor, @entity,
                    @entityId, @fieldPath, @fieldClass, @before,
                    @after, @prev, @hash, @createdUtc)
            """,
            new
            {
                eventId,
                appId = d.ApplicationId,
                kind = d.EventKind,
                actor = d.Actor,
                entity = d.Entity,
                entityId = d.EntityId,
                fieldPath = d.FieldPath,
                fieldClass = d.FieldClass?.ToString(),
                before = d.BeforeVal,
                after = d.AfterVal,
                prev = prevHash,
                hash,
                createdUtc
            }, cancellationToken: ct));

    public async Task<EventVerifyResult> VerifyChainAsync(
        CancellationToken ct = default)
    {
        var rows = await QueryAsync<Row>(
            $"SELECT {Cols} FROM loan_events ORDER BY seq", ct: ct);
        var events = rows.Select(r => r.ToRecord()).ToList();
        var broken = EventChain.FirstBroken(events);
        return new EventVerifyResult(broken is null, broken, events.Count);
    }

    public async Task<IReadOnlyList<LoanEvent>> QueryAsync(
        string applicationId, int limit = 200,
        CancellationToken ct = default)
    {
        var rows = await QueryAsync<Row>(
            $"SELECT {Cols} FROM loan_events"
            + " WHERE application_id = @appId"
            + " ORDER BY seq DESC LIMIT @limit",
            new { appId = applicationId, limit }, ct);
        return rows.Select(r => r.ToRecord()).ToList();
    }
}
