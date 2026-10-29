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

    /// Test-only stand-in for a raw UPDATE against the audit table:
    /// flips a stored row's content without touching its hash, so
    /// VerifyChain must report that seq as broken — the same detection
    /// the SQLCipher log's tamper test proves in Lola.DataPlane.Tests.
    public void TamperForTest(long seq = 1)
    {
        lock (_events)
        {
            var i = _events.FindIndex(e => e.Seq == seq);
            if (i < 0)
                throw new InvalidOperationException($"no audit row at seq {seq}");
            _events[i] = _events[i] with { Action = _events[i].Action + "-x" };
        }
    }

    public Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditFilter filter,
        CancellationToken ct = default)
    {
        lock (_events)
            return Task.FromResult<IReadOnlyList<AuditEvent>>(_events
                .Where(e =>
                    (filter.Entity is null || e.Entity == filter.Entity) &&
                    (filter.EntityId is null || e.EntityId == filter.EntityId) &&
                    (filter.Actor is null || e.Actor == filter.Actor) &&
                    (filter.Action is null || e.Action == filter.Action) &&
                    (filter.Since is null || string.CompareOrdinal(
                        e.CreatedUtc, filter.Since) >= 0) &&
                    (filter.Until is null || string.CompareOrdinal(
                        e.CreatedUtc, filter.Until) <= 0))
                .ToList());
    }
}
