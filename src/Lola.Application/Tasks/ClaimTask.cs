using Lola.Application.Admin;
using Lola.Application.Audit;
using Lola.Application.Errors;

namespace Lola.Application.Tasks;

/// Claim a queue task — open → claimed under the acting officer.
/// Idempotent on self-claim (retry-safe); another officer's claim or
/// a terminal state is a 409. Tenant-scoped: a task id from another
/// tenant is a 404, not a cross-tenant read. Every successful claim
/// lands on the control-plane audit spine (invariant 4).
public sealed record ClaimTaskCommand(string TenantId, string TaskId,
    string ActorId, bool ActorIsBorrower);

public static class ClaimTaskHandler
{
    public static async Task<QueueTask> Handle(ClaimTaskCommand c,
        ITaskRepository tasks, IControlPlaneAuditLog audit,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw DomainException.Forbidden(
                "claiming tasks is staff-only");
        var existing = await tasks.GetAsync(c.TaskId, ct);
        if (existing is null || existing.TenantId != c.TenantId)
            throw DomainException.NotFound("task not found");
        if (existing.State == TaskState.Claimed
            && existing.ClaimedBy == c.ActorId)
            return existing;                    // idempotent self-claim
        var claimed = await tasks.ClaimAsync(c.TaskId, c.ActorId, ct);
        if (claimed is null)
            throw DomainException.Conflict(existing.State switch
            {
                TaskState.Claimed => "task is already claimed",
                _ => $"task is '{existing.State}' and cannot be claimed"
            });
        await audit.WriteAsync(c.ActorId, "task_claimed", "task",
            c.TaskId, ["state"], $"claimed in {c.TenantId}", ct);
        return claimed;
    }
}
