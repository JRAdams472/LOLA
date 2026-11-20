using Lola.Application.Admin;
using Lola.Application.Audit;
using Lola.Application.Errors;

namespace Lola.Application.Tasks;

/// Complete a queue task — open|claimed → done, audited. A task
/// claimed by a DIFFERENT officer is a 409 (the claimer owns the
/// completion); terminal states are 400. Done rows leave the live
/// queue and the dedupe slot frees for re-derivation.
public sealed record CompleteTaskCommand(string TenantId, string TaskId,
    string ActorId, bool ActorIsBorrower);

public static class CompleteTaskHandler
{
    public static async Task<QueueTask> Handle(CompleteTaskCommand c,
        ITaskRepository tasks, IControlPlaneAuditLog audit,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw DomainException.Forbidden(
                "completing tasks is staff-only");
        var existing = await tasks.GetAsync(c.TaskId, ct);
        if (existing is null || existing.TenantId != c.TenantId)
            throw DomainException.NotFound("task not found");
        if (existing.State == TaskState.Claimed
            && existing.ClaimedBy != c.ActorId)
            throw DomainException.Conflict(
                "task is claimed by another officer");
        var done = await tasks.CompleteAsync(c.TaskId, c.ActorId, ct);
        if (done is null)
            throw DomainException.BadRequest(
                $"task is '{existing.State}' and cannot be completed");
        await audit.WriteAsync(c.ActorId, "task_completed", "task",
            c.TaskId, ["state"], $"completed in {c.TenantId}", ct);
        return done;
    }
}
