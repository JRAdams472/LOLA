using System.Security.Claims;
using HotChocolate.Resolvers;
using Lola.Api.Identity;
using Lola.Application.Admin.Groups;
using Lola.Application.Admin.Users;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Documents;
using Lola.Application.Fees;
using Lola.Application.Messaging;
using Lola.Application.Pricing;
using Lola.Application.Security;
using Lola.Application.Vault;
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

    /// Blind-index equality search (M11) — staff-only inside the
    /// handler; the value travels in the POST body, never a URL.
    public Task<ApplicationSearchResult> Search(
        string tenantId, string field, string value,
        IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<ApplicationSearchResult>(
            new SearchApplicationsQuery(tenantId, field, value, actor, isBorrower), ct);
    }

    public Task<PricingEvalDto?> Pricing(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<PricingEvalDto?>(
            new GetApplicationPricingQuery(tenantId, appId, actor, isBorrower), ct);
    }

    public Task<IReadOnlyList<PricingEvalDto>> PricingHistory(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<PricingEvalDto>>(
            new GetPricingHistoryQuery(tenantId, appId, actor, isBorrower), ct);
    }

    public Task<FeeComparisonDto?> Fees(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<FeeComparisonDto?>(
            new GetApplicationFeesQuery(tenantId, appId, actor, isBorrower), ct);
    }

    public Task<IReadOnlyList<FeeEvalDto>> FeeHistory(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<FeeEvalDto>>(
            new GetFeeHistoryQuery(tenantId, appId, actor, isBorrower), ct);
    }

    public Task<RateLockDto?> RateLock(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<RateLockDto?>(
            new GetRateLockQuery(tenantId, appId, actor, isBorrower), ct);
    }

    public Task<IReadOnlyList<DocDto>> Documents(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<DocDto>>(
            new GetApplicationDocumentsQuery(tenantId, appId, actor, isBorrower), ct);
    }

    public Task<IReadOnlyList<VaultDocDto>> Uploads(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<VaultDocDto>>(
            new GetApplicationUploadsQuery(tenantId, appId, actor, isBorrower), ct);
    }

    public Task<IReadOnlyList<StepEventDto>> Steps(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<StepEventDto>>(
            new GetApplicationStepsQuery(tenantId, appId, actor, isBorrower), ct);
    }

    public Task<IReadOnlyList<AuditEvent>> ApplicationAudit(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<AuditEvent>>(
            new GetApplicationAuditQuery(tenantId, appId, actor, isBorrower), ct);
    }

    public Task<IReadOnlyList<AuditEvent>> TenantAudit(
        string tenantId, IResolverContext context, CancellationToken ct,
        string? actor = null, string? action = null,
        string? since = null, string? until = null)
    {
        var (actorId, isBorrower) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<AuditEvent>>(
            new GetTenantAuditQuery(tenantId, actorId, isBorrower,
                actor, action, since, until), ct);
    }

    public Task<IReadOnlyList<GroupDto>> TenantGroups(
        string tenantId, IResolverContext context, CancellationToken ct)
    {
        var (actor, _) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<GroupDto>>(
            new ListGroupsQuery(tenantId), ct);
    }

    /// Member ids of one group — the admin membership dialog joins
    /// them against the tenant user list (MPE-211).
    public Task<IReadOnlyList<string>> GroupMembers(
        string tenantId, string groupId, IResolverContext context,
        CancellationToken ct)
    {
        var (actor, _) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<string>>(
            new ListGroupMembersQuery(tenantId, groupId), ct);
    }

    public Task<TenantRetentionReport> Retention(
        string tenantId, IResolverContext context, CancellationToken ct)
    {
        var (actor, isBorrower) = Actor(context);
        return bus.InvokeAsync<TenantRetentionReport>(
            new GetTenantRetentionQuery(tenantId, actor, isBorrower), ct);
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
