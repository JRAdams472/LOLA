using Dapper;
using Lola.Application.Audit;
using System.Text.Json;

namespace Lola.DataPlane;

/// Tenant-file audit log (M6 P1) — PII activity inside this tenant's
/// SQLCipher file lands here: application reads/writes, blind searches,
/// exports, reveals. Append-only + hash-chained; a tampered row breaks
/// VerifyChainAsync at that seq.
public sealed class TenantAuditLog(TenantConnectionFactory factory)
    : TenantRepository(factory), ITenantAuditLog
{
    public async Task WriteAsync(string actor, string action,
        string entity, string? entityId,
        IReadOnlyList<string> fieldClasses, string? reason,
        CancellationToken ct = default)
    {
        var eventId = $"evt-{Guid.NewGuid():N}";
        var classesJson = JsonSerializer.Serialize(fieldClasses);
        var createdUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await using var conn = await OpenAsync(ct);
        // BEGIN IMMEDIATE serializes concurrent writers: the last-hash
        // read and the insert must be one atomic step or two writers
        // could read the same prev_hash and fork the chain. WAL's
        // busy_timeout pragma makes the loser wait, not fail.
        await conn.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            var prev = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT hash FROM audit_events ORDER BY seq DESC LIMIT 1")
                ?? HashChain.Genesis;
            var hash = HashChain.ComputeHash(prev, eventId, actor, action,
                entity, entityId, fieldClasses, reason, createdUtc);
            await conn.ExecuteAsync(
                """
                INSERT INTO audit_events
                    (event_id, actor, action, entity, entity_id,
                     field_classes, reason, prev_hash, hash, created_utc)
                VALUES (@eventId, @actor, @action, @entity, @entityId,
                        @classesJson, @reason, @prev, @hash, @createdUtc)
                """,
                new
                {
                    eventId,
                    actor,
                    action,
                    entity,
                    entityId,
                    classesJson,
                    reason,
                    prev,
                    hash,
                    createdUtc
                });
            await conn.ExecuteAsync("COMMIT");
        }
        catch
        {
            await conn.ExecuteAsync("ROLLBACK");
            throw;
        }
    }

    public async Task<AuditVerifyResult> VerifyChainAsync(
        CancellationToken ct = default)
    {
        var events = await QueryAsync(new AuditFilter(), ct);
        var broken = HashChain.FirstBroken(events);
        return new AuditVerifyResult(broken is null, broken, events.Count);
    }

    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(
        AuditFilter filter, CancellationToken ct = default)
    {
        var rows = await QueryAsync<Row>(
            """
            SELECT seq AS Seq, event_id AS EventId, actor AS Actor,
                   action AS Action, entity AS Entity, entity_id AS EntityId,
                   field_classes AS FieldClassesJson, reason AS Reason,
                   prev_hash AS PrevHash, hash AS Hash, created_utc AS CreatedUtc
            FROM audit_events
            WHERE (@entity IS NULL OR entity = @entity)
              AND (@entityId IS NULL OR entity_id = @entityId)
              AND (@actor IS NULL OR actor = @actor)
              AND (@action IS NULL OR action = @action)
              AND (@since IS NULL OR created_utc >= @since)
              AND (@until IS NULL OR created_utc <= @until)
            ORDER BY seq
            """, new
            {
                entity = filter.Entity,
                entityId = filter.EntityId,
                actor = filter.Actor,
                action = filter.Action,
                since = filter.Since,
                until = filter.Until
            }, ct);
        return rows.Select(r => new AuditEvent(r.Seq, r.EventId, r.Actor,
            r.Action, r.Entity, r.EntityId,
            JsonSerializer.Deserialize<IReadOnlyList<string>>(r.FieldClassesJson) ?? [],
            r.Reason, r.PrevHash, r.Hash, r.CreatedUtc)).ToList();
    }

    private sealed record Row(long Seq, string EventId, string Actor,
        string Action, string Entity, string? EntityId,
        string FieldClassesJson, string? Reason, string PrevHash,
        string Hash, string CreatedUtc);
}
