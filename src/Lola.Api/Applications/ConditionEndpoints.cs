using System.Security.Claims;
using Lola.Api.Identity;
using Lola.Application.Events;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Http;
using Wolverine;

namespace Lola.Api.Applications;

/// Conditions + mutation-event read surface (M15 P3). Mutations and
/// staff detail are TenantStaff; the conditions list admits borrowers
/// who get the stripped M12-style projection (own application, public
/// fields only). The event list and chain verify are staff-only —
/// rows carry blind-index tokens, never values.
public static class ConditionEndpoints
{
    public static void MapConditionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tenants/{id}/applications")
            .RequireAuthorization(LolaPolicies.Applications)
            .RequireRateLimiting("intake");

        // List — borrowers reach the public projection; staff get
        // full detail including internal notes and linkage. Partners
        // are refused for now — their conditions view arrives with the
        // partner surface in M19 P2.
        group.MapGet("/{appId}/conditions", async (string id,
                string appId, string? state, ClaimsPrincipal user,
                IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => LolaPolicies.ScopeFor(user) switch
            {
                Lola.Application.Applications.ActorScope.Borrower =>
                    await bus.InvokeAsync<
                        IReadOnlyList<BorrowerConditionDto>>(
                        new ListBorrowerConditionsQuery(id, appId,
                            Actor(user)), ct) as object,
                Lola.Application.Applications.ActorScope.Staff =>
                    await bus.InvokeAsync<IReadOnlyList<ConditionDto>>(
                        new ListConditionsQuery(id, appId, Actor(user),
                            Lola.Application.Applications.ActorScope.Staff,
                            state), ct) as object,
                _ => throw Lola.Application.Errors.DomainException
                    .Forbidden("partners cannot read conditions")
            }));

        // Mutations — staff-only.
        group.MapPost("/{appId}/conditions", async (string id,
                string appId, OpenConditionRequest body,
                ClaimsPrincipal user, IMessageBus bus,
                CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<ConditionDto>(
                new OpenConditionCommand(id, appId, Actor(user),
                    LolaPolicies.ScopeFor(user), body.Kind ?? "manual", body.Title,
                    body.Detail, body.Blocking ?? false, body.LinkedEntity,
                    body.LinkedEntityId), ct)))
            .RequireAuthorization(LolaPolicies.TenantStaff);

        group.MapPost("/{appId}/conditions/{cid}/close", async (
                string id, string appId, string cid,
                TransitionConditionRequest body,
                ClaimsPrincipal user, IMessageBus bus,
                CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<ConditionDto>(
                new TransitionConditionCommand(id, appId, cid,
                    Actor(user), LolaPolicies.ScopeFor(user), "closed",
                    body.Reason), ct)))
            .RequireAuthorization(LolaPolicies.TenantStaff);

        group.MapPost("/{appId}/conditions/{cid}/waive", async (
                string id, string appId, string cid,
                TransitionConditionRequest body,
                ClaimsPrincipal user, IMessageBus bus,
                CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<ConditionDto>(
                new TransitionConditionCommand(id, appId, cid,
                    Actor(user), LolaPolicies.ScopeFor(user), "waived",
                    body.Reason), ct)))
            .RequireAuthorization(LolaPolicies.TenantStaff);

        group.MapPost("/{appId}/conditions/{cid}/reopen", async (
                string id, string appId, string cid,
                ClaimsPrincipal user, IMessageBus bus,
                CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<ConditionDto>(
                new TransitionConditionCommand(id, appId, cid,
                    Actor(user), LolaPolicies.ScopeFor(user), "open", null), ct)))
            .RequireAuthorization(LolaPolicies.TenantStaff);

        // Mutation event spine — staff-only (blind-index tokens only).
        group.MapGet("/{appId}/events", async (string id, string appId,
                int? limit, ClaimsPrincipal user, IMessageBus bus,
                CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<
                IReadOnlyList<LoanEvent>>(
                new GetLoanEventsQuery(id, appId, Actor(user),
                    LolaPolicies.ScopeFor(user), limit ?? 200), ct)))
            .RequireAuthorization(LolaPolicies.TenantStaff);

        // Tenant-level event chain verify — mirrors /audit/verify.
        app.MapGet("/api/tenants/{id}/events/verify", async (string id,
                ClaimsPrincipal user, IMessageBus bus,
                CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<EventVerifyResult>(
                new VerifyEventChainQuery(id, Actor(user),
                    LolaPolicies.ScopeFor(user)), ct)))
            .RequireAuthorization(LolaPolicies.TenantStaff);
    }

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
}

public sealed record OpenConditionRequest(
    string? Kind, string Title, string? Detail, bool? Blocking,
    string? LinkedEntity, string? LinkedEntityId);

public sealed record TransitionConditionRequest(string? Reason);
