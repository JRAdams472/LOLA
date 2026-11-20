using System.Security.Claims;
using Lola.Api.Identity;
using Lola.Application.Tasks;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Http;
using Wolverine;

namespace Lola.Api.Tasks;

/// Officer work-queue surface (M16 P3). Entirely TenantStaff — the
/// queue is a cross-application officer surface, so there is no
/// borrower projection; borrower tokens get the policy 403 before
/// the handler. Rows are PII-free by construction (refs + metadata).
public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tenants/{id}/tasks")
            .RequireAuthorization(LolaPolicies.TenantStaff)
            .RequireRateLimiting("intake");

        // Ranked live queue — lazy tenant eval runs in the handler so
        // time-driven tasks stay current. ?skills=a,b narrows to
        // matching tags (+ untagged general work).
        group.MapGet("/", async (string id, string? skills,
                ClaimsPrincipal user, IMessageBus bus,
                CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<
                IReadOnlyList<QueueTask>>(
                new ListTasksQuery(id, Actor(user), IsBorrower(user),
                    ParseSkills(skills)), ct)));

        // Next-best-task — the single highest-priority row.
        group.MapGet("/next", async (string id, string? skills,
                ClaimsPrincipal user, IMessageBus bus,
                CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<QueueTask?>(
                new GetNextTaskQuery(id, Actor(user), IsBorrower(user),
                    ParseSkills(skills)), ct)));

        // Claim/complete — audited, state-guarded transitions.
        group.MapPost("/{taskId}/claim", async (string id,
                string taskId, ClaimsPrincipal user, IMessageBus bus,
                CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<QueueTask>(
                new ClaimTaskCommand(id, taskId, Actor(user),
                    IsBorrower(user)), ct)));

        group.MapPost("/{taskId}/complete", async (string id,
                string taskId, ClaimsPrincipal user, IMessageBus bus,
                CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<QueueTask>(
                new CompleteTaskCommand(id, taskId, Actor(user),
                    IsBorrower(user)), ct)));
    }

    private static IReadOnlyList<string>? ParseSkills(string? skills)
        => string.IsNullOrWhiteSpace(skills) ? null
            : skills.Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);

    private static async Task<IResult> Guarded(string tenantId,
        Func<Task<object?>> work)
    {
        if (Lola.Application.Tenancy.TenantContext.Current?.TenantId
            != tenantId)
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "request tenant does not match the resolved tenant");
        var result = await work();
        return Results.Ok(result);
    }

    private static string Actor(ClaimsPrincipal user)
        => user.FindFirst("sub")?.Value ?? "unknown";

    private static bool IsBorrower(ClaimsPrincipal user)
        => LolaPolicies.HasRole(user, LolaRole.Borrower);
}
