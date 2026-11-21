using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;

namespace Lola.Application.Tasks;

/// The single highest-priority live task — the "next best task" an
/// officer should work. Same lazy refresh + ranking as the list;
/// honors the same optional skill filter so callers can ask "next
/// for me". Null when the queue is empty.
public sealed record GetNextTaskQuery(string TenantId, string ActorId,
    ActorScope Scope, IReadOnlyList<string>? Skills = null);

public static class GetNextTaskHandler
{
    public static async Task<QueueTask?> Handle(GetNextTaskQuery q,
        ITaskRepository tasks, TaskEvaluator eval,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope != ActorScope.Staff)
            throw DomainException.Forbidden(
                "the work queue is staff-only");
        await eval.EvaluateTenantAsync(q.TenantId, ct);
        return (await tasks.ListQueueAsync(q.TenantId, q.Skills, ct))
            .FirstOrDefault();
    }
}
