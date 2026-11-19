using Lola.Application.Events;

namespace Lola.Api.Tests;

/// Hermetic loan-event log (M15) — mirrors FakeTenantAuditLog: keeps
/// the hash chain real so api tests observe the same shapes the
/// SQLCipher implementation produces.
public sealed class FakeLoanEventLog : ILoanEventLog
{
    private readonly List<LoanEvent> _events = [];
    private string _prev = Lola.Application.Audit.HashChain.Genesis;

    public IReadOnlyList<LoanEvent> Events => _events;

    public Task WriteAsync(LoanEventDraft draft,
        CancellationToken ct = default)
    {
        var eventId = $"lev-{Guid.NewGuid():N}";
        var createdUtc = DateTimeOffset.UtcNow
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var hash = EventChain.ComputeHash(_prev, eventId,
            draft.ApplicationId, draft.EventKind, draft.Actor,
            draft.Entity, draft.EntityId, draft.FieldPath,
            draft.FieldClass?.ToString(), draft.BeforeVal,
            draft.AfterVal, createdUtc);
        lock (_events)
            _events.Add(new LoanEvent(_events.Count + 1, eventId,
                draft.ApplicationId, draft.EventKind, draft.Actor,
                draft.Entity, draft.EntityId, draft.FieldPath,
                draft.FieldClass?.ToString(), draft.BeforeVal,
                draft.AfterVal, _prev, hash, createdUtc));
        _prev = hash;
        return Task.CompletedTask;
    }

    private readonly Dictionary<long, string?> _tampered = [];

    /// Flip one row's stored value so VerifyChainAsync reports the seq
    /// the real SQLCipher store would flag on tamper. Pair with
    /// <see cref="UntamperForTest"/> — the fake is a singleton shared
    /// across tests in the fixture, so a left-broken chain pollutes
    /// later verifies.
    public void TamperForTest(long seq = 1)
    {
        lock (_events)
        {
            var i = _events.FindIndex(e => e.Seq == seq);
            _tampered[seq] = _events[i].AfterVal;
            _events[i] = _events[i] with
            { AfterVal = (_events[i].AfterVal ?? "") + "x" };
        }
    }

    /// Undo a TamperForTest so the chain verifies clean again.
    public void UntamperForTest(long seq = 1)
    {
        lock (_events)
        {
            var i = _events.FindIndex(e => e.Seq == seq);
            _events[i] = _events[i] with { AfterVal = _tampered[seq] };
            _tampered.Remove(seq);
        }
    }

    public Task<EventVerifyResult> VerifyChainAsync(
        CancellationToken ct = default)
    {
        var broken = EventChain.FirstBroken(_events);
        return Task.FromResult(
            new EventVerifyResult(broken is null, broken, _events.Count));
    }

    public Task<IReadOnlyList<LoanEvent>> QueryAsync(string applicationId,
        int limit = 200, CancellationToken ct = default)
    {
        List<LoanEvent> copy;
        lock (_events)
            copy = _events.Where(e => e.ApplicationId == applicationId)
                .OrderByDescending(e => e.Seq).Take(limit).ToList();
        return Task.FromResult<IReadOnlyList<LoanEvent>>(copy);
    }
}

/// Hermetic conditions store (M15) — in-memory lifecycle with the
/// same state-machine guards as the SQLCipher implementation.
public sealed class FakeConditionRepository : IConditionRepository
{
    private readonly List<Condition> _all = [];

    public Task<Condition> OpenAsync(ConditionDraft draft,
        CancellationToken ct = default)
    {
        var c = new Condition(
            $"cond-{Guid.NewGuid():N}",
            draft.ApplicationId, draft.Kind, "open", draft.Blocking,
            draft.LinkedEntity, draft.LinkedEntityId, draft.Title,
            draft.Detail, draft.OpenedBy,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            null, null, null);
        _all.Add(c);
        return Task.FromResult(c);
    }

    public Task<Condition?> GetAsync(string conditionId,
        CancellationToken ct = default)
        => Task.FromResult(_all.FirstOrDefault(
            c => c.ConditionId == conditionId));

    public Task<IReadOnlyList<Condition>> ListByApplicationAsync(
        string applicationId, string? state = null,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Condition>>(_all
            .Where(c => c.ApplicationId == applicationId
                && (state is null || c.State == state)).ToList());

    public Task<Condition> TransitionAsync(string conditionId,
        string to, string? reason, CancellationToken ct = default)
    {
        var c = _all.First(x => x.ConditionId == conditionId);
        Condition next;
        if (to == "waived")
            next = c with
            {
                State = "waived",
                ClosedUtc = DateTimeOffset.UtcNow
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                WaiverReason = reason
            };
        else if (to == "closed")
            next = c with
            {
                State = "closed",
                ClosedUtc = DateTimeOffset.UtcNow
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                CloseReason = reason
            };
        else if (to == "open")
            next = c with { State = "open", ClosedUtc = null };
        else throw new ArgumentException($"bad condition state '{to}'");
        _all[_all.IndexOf(c)] = next;
        return Task.FromResult(next);
    }
}
