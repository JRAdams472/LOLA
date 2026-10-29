using Dapper;
using Lola.Application.Audit;
using System.Text.Json;

namespace Lola.ControlPlane;

/// Control-plane audit log (M6 P1) — events that must outlive tenant
/// files: identity PII access, admin actions, retention overrides,
/// erasure, residual anonymization. Same append-only hash-chained
/// shape as the tenant-file log.
public sealed class ControlPlaneAuditLog(ControlPlaneDb db)
    : IControlPlaneAuditLog
{
    public async Task WriteAsync(string actor, string action,
        string entity, string? entityId,
        IReadOnlyList<string> fieldClasses, string? reason,
        CancellationToken ct = default)
    {
        var eventId = $"evt-{Guid.NewGuid():N}";
        var classesJson = JsonSerializer.Serialize(fieldClasses);
        var createdUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await using var conn = db.Open();
        // Same atomic read+insert discipline as TenantAuditLog —
        // BEGIN IMMEDIATE serializes writers so the chain can't fork.
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
        await using var conn = db.Open();
        var rows = await conn.QueryAsync<Row>(
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
            });
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
