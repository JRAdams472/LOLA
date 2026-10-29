using System.Security.Claims;
using HotChocolate.Resolvers;
using Lola.Api.Identity;
using Lola.Application.Admin.Users;
using Lola.Application.Applications;
using Lola.Application.Messaging;
using Lola.Application.Security;
using Lola.Domain.Identity;
using Wolverine;

namespace Lola.Api.GraphQL;

public class Query(IMessageBus bus)
{
    public Task<string> Ping(CancellationToken cancellationToken)
        => bus.InvokeAsync<string>(new PingQuery(), cancellationToken);

    /// Same masked invoke seam as REST /api/admin/tenants/{id}/users —
    /// masking is identical by construction (ADR-0011).
    public Task<IReadOnlyList<AdminUserDto>> TenantUsers(
        string tenantId, IResolverContext context, CancellationToken cancellationToken)
        => bus.InvokeMaskedAsync<IReadOnlyList<AdminUserDto>>(
            new GetTenantUsersQuery(tenantId, Actor(context).ActorId), cancellationToken);

    /// Intake reads (MPE-110) — same handlers as REST; the doc's PII
    /// is masked inside the handler per VisibilityContext. `docJson`
    /// serializes the JSON1 payload — JsonObject can't map to a
    /// schema type, and a string keeps the document opaque anyway.
    public async Task<ApplicationView?> Application(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        var dto = await bus.InvokeAsync<ApplicationDto?>(
            new GetApplicationQuery(tenantId, appId, actor, isBorrower), ct);
        return dto is null ? null : new ApplicationView(
            dto.Id, dto.Status, dto.PipelineStep, dto.BorrowerUserId,
            dto.CurrentVersion, dto.TridClockStart, dto.TridDeadlineUtc,
            dto.SubmittedUtc, dto.Doc.ToJsonString());
    }

    public Task<IReadOnlyList<ApplicationMeta>> Applications(
        string tenantId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<ApplicationMeta>>(
            new ListApplicationsQuery(tenantId, actor, isBorrower), ct);
    }

    private static (string ActorId, bool IsBorrower) Actor(IResolverContext context)
    {
        var user = context.GetUser() ?? new ClaimsPrincipal();
        return (user.FindFirst("sub")?.Value ?? "unknown",
            LolaPolicies.HasRole(user, LolaRole.Borrower));
    }
}

/// GraphQL-facing application view — the payload stays an opaque
/// document string (masked upstream by DocMasker).
public sealed record ApplicationView(
    string Id, string Status, string PipelineStep, string? BorrowerUserId,
    int CurrentVersion, string? TridClockStart, string? TridDeadlineUtc,
    string? SubmittedUtc, string DocJson);
