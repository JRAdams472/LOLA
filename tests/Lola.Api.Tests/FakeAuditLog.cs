using Lola.Application.Audit;

namespace Lola.Api.Tests;

/// In-memory tenant audit log for the API host — stores real chained
/// events (same HashChain mechanics as the SQLCipher-backed log) so
/// emission wiring and the verify endpoint are proven hermetically.
public sealed class FakeTenantAuditLog : ITenantAuditLog
{
    private readonly List<AuditEvent> _events = [];
    private string _prev = HashChain.Genesis;

    public Task WriteAsync(string actor, string action, string entity,
        string? entityId, IReadOnlyList<string> fieldClasses,
        string? reason, CancellationToken ct = default)
    {
        var eventId = $"evt-{Guid.NewGuid():N}";
        var createdUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var hash = HashChain.ComputeHash(_prev, eventId, actor, action,
            entity, entityId, fieldClasses, reason, createdUtc);
        lock (_events)
            _events.Add(new AuditEvent(_events.Count + 1, eventId, actor,
                action, entity, entityId, fieldClasses, reason, _prev,
                hash, createdUtc));
        _prev = hash;
        return Task.CompletedTask;
    }

    public Task<AuditVerifyResult> VerifyChainAsync(
        CancellationToken ct = default)
    {
        List<AuditEvent> snapshot;
        lock (_events) snapshot = [.. _events];
        var broken = HashChain.FirstBroken(snapshot);
        return Task.FromResult(new AuditVerifyResult(
            broken is null, broken, snapshot.Count));
    }

    public Task<IReadOnlyList<AuditEvent>> QueryAsync(string? entity = null,
        string? entityId = null, CancellationToken ct = default)
    {
        lock (_events)
            return Task.FromResult<IReadOnlyList<AuditEvent>>(_events
                .Where(e => (entity is null || e.Entity == entity) &&
                            (entityId is null || e.EntityId == entityId))
                .ToList());
    }
}
