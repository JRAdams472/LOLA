using Lola.Application.Events;
using Lola.Application.Tasks;

namespace Lola.Application.Tests;

/// In-memory task store for evaluator tests — records every upsert
/// and resolve call so assertions can inspect the emitted drafts.
public sealed class FakeTaskRepository : ITaskRepository
{
    private long _seq;
    public List<QueueTask> Store { get; } = [];
    public List<(string AppId, string Entity, string EntityId)> Resolves
    { get; } = [];

    public Task<QueueTask> UpsertAsync(TaskDraft draft,
        CancellationToken ct = default)
    {
        var key = draft.BlockingEntityId ?? "";
        var live = Store.FirstOrDefault(t =>
            t.TenantId == draft.TenantId
            && t.ApplicationId == draft.ApplicationId
            && t.Kind == draft.Kind
            && (t.BlockingEntityId ?? "") == key
            && t.State is TaskState.Open or TaskState.Claimed);
        if (live is not null)
        {
            var i = Store.IndexOf(live);
            Store[i] = live with
            {
                Title = draft.Title,
                Priority = draft.Priority,
                SkillTags = draft.SkillTags ?? [],
                DueUtc = draft.DueUtc,
                BlockingEntity = draft.BlockingEntity
            };
            return Task.FromResult(Store[i]);
        }
        var task = new QueueTask(++_seq, $"task-{_seq}", draft.TenantId,
            draft.ApplicationId, draft.Kind, draft.Title, draft.Priority,
            draft.SkillTags ?? [], draft.DueUtc, TaskState.Open,
            draft.BlockingEntity, draft.BlockingEntityId, null,
            "2026-01-01T00:00:00.000Z", null, null,
            "2026-01-01T00:00:00.000Z");
        Store.Add(task);
        return Task.FromResult(task);
    }

    public Task<QueueTask?> GetAsync(string taskId,
        CancellationToken ct = default)
        => Task.FromResult(Store.FirstOrDefault(t => t.TaskId == taskId));

    public Task<IReadOnlyList<QueueTask>> ListQueueAsync(string tenantId,
        IReadOnlyList<string>? skills = null,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<QueueTask>>(Store
            .Where(t => t.TenantId == tenantId
                && t.State is TaskState.Open or TaskState.Claimed)
            .ToList());

    public Task<QueueTask?> ClaimAsync(string taskId, string actor,
        CancellationToken ct = default) => Transition(taskId,
        TaskState.Open, t => t with
        { State = TaskState.Claimed, ClaimedBy = actor });

    public Task<QueueTask?> CompleteAsync(string taskId, string actor,
        CancellationToken ct = default) => Transition(taskId,
        null, t => t with { State = TaskState.Done });

    public Task<int> ResolveBySourceAsync(string tenantId,
        string applicationId, string blockingEntity,
        string blockingEntityId, CancellationToken ct = default)
    {
        Resolves.Add((applicationId, blockingEntity, blockingEntityId));
        var n = Store.RemoveAll(t => t.TenantId == tenantId
            && t.ApplicationId == applicationId
            && t.BlockingEntity == blockingEntity
            && (t.BlockingEntityId ?? "") == blockingEntityId
            && t.State is TaskState.Open or TaskState.Claimed);
        return Task.FromResult(n);
    }

    private Task<QueueTask?> Transition(string taskId, string? fromState,
        Func<QueueTask, QueueTask> apply)
    {
        var t = Store.FirstOrDefault(x => x.TaskId == taskId
            && (fromState is null
                || x.State == fromState
                || x.State == TaskState.Open));
        if (t is null) return Task.FromResult<QueueTask?>(null);
        var i = Store.IndexOf(t);
        Store[i] = apply(t);
        return Task.FromResult<QueueTask?>(Store[i]);
    }
}

/// In-memory conditions — evaluator tests seed directly.
public sealed class FakeConditionRepository : IConditionRepository
{
    public List<Condition> Store { get; } = [];
    private int _n;

    public Task<Condition> OpenAsync(ConditionDraft draft,
        CancellationToken ct = default)
    {
        var c = new Condition($"cond-{++_n}", draft.ApplicationId,
            draft.Kind, ConditionState.Open, draft.Blocking,
            draft.LinkedEntity, draft.LinkedEntityId, draft.Title,
            draft.Detail, draft.OpenedBy, "2026-01-01T00:00:00.000Z",
            null, null, null);
        Store.Add(c);
        return Task.FromResult(c);
    }

    public Task<Condition?> GetAsync(string conditionId,
        CancellationToken ct = default)
        => Task.FromResult(Store.FirstOrDefault(
            c => c.ConditionId == conditionId));

    public Task<IReadOnlyList<Condition>> ListByApplicationAsync(
        string applicationId, string? state = null,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Condition>>(Store
            .Where(c => c.ApplicationId == applicationId
                && (state is null || c.State == state))
            .ToList());

    public Task<Condition> TransitionAsync(string conditionId,
        string toState, string? reason, CancellationToken ct = default)
    {
        var c = Store.First(x => x.ConditionId == conditionId);
        var i = Store.IndexOf(c);
        Store[i] = c with { State = toState };
        return Task.FromResult(Store[i]);
    }
}
