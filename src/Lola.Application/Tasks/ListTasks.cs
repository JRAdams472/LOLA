using Lola.Application.Admin;
using Lola.Application.Errors;

namespace Lola.Application.Tasks;

/// Ranked live queue for a tenant — staff-only. Runs the lazy
/// tenant-wide evaluation first so time-driven tasks (TRID proximity,
/// stale drafts) are current at read time without a worker.
public sealed record ListTasksQuery(string TenantId, string ActorId,
    bool ActorIsBorrower, IReadOnlyList<string>? Skills = null);

public static class ListTasksHandler
{
    public static async Task<IReadOnlyList<QueueTask>> Handle(
        ListTasksQuery q, ITaskRepository tasks, TaskEvaluator eval,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden(
                "the work queue is staff-only");
        await eval.EvaluateTenantAsync(q.TenantId, ct);
        return await tasks.ListQueueAsync(q.TenantId, q.Skills, ct);
    }
}
