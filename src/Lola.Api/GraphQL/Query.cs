using System.Security.Claims;
using HotChocolate.Resolvers;
using Lola.Api.Identity;
using Lola.Application.Admin.Groups;
using Lola.Application.Admin.Users;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Documents;
using Lola.Application.Events;
using Lola.Application.Fees;
using Lola.Application.Messaging;
using Lola.Application.Portal;
using Lola.Application.Pricing;
using Lola.Application.Security;
using Lola.Application.Vault;
using Lola.Application.Vendors;
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
    {
        var (actor, scope) = Actor(context);
        return bus.InvokeMaskedAsync<IReadOnlyList<AdminUserDto>>(
            new GetTenantUsersQuery(tenantId, actor, scope), cancellationToken);
    }

    /// Intake reads (MPE-110) — same handlers as REST; the doc's PII
    /// is masked inside the handler per VisibilityContext. `docJson`
    /// serializes the JSON1 payload — JsonObject can't map to a
    /// schema type, and a string keeps the document opaque anyway.
    public async Task<ApplicationView?> Application(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return null;   // officer surface — portal uses portalLoan
        var dto = await bus.InvokeAsync<ApplicationDto?>(
            new GetApplicationQuery(tenantId, appId, actor, scope), ct);
        return dto is null ? null : new ApplicationView(
            dto.Id, dto.Status, dto.PipelineStep, dto.BorrowerUserId,
            dto.CurrentVersion, dto.TridClockStart, dto.TridDeadlineUtc,
            dto.SubmittedUtc, dto.Doc.ToJsonString());
    }

    public Task<IReadOnlyList<ApplicationMeta>> Applications(
        string tenantId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return Task.FromResult<IReadOnlyList<ApplicationMeta>>([]);
        return bus.InvokeAsync<ApplicationListPage>(
            new ListApplicationsQuery(tenantId, actor, scope), ct)
            .ContinueWith(t => (IReadOnlyList<ApplicationMeta>)t.Result.Items);
    }

    /// Borrower-portal reads (M12) — own application only, borrower
    /// vocabulary only (PortalLoanDto carries no internal fields).
    /// Staff actors get null — the portal surface is borrower-scope
    /// by construction.
    public async Task<PortalLoanView?> PortalLoan(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Borrower) return null;
        var dto = await bus.InvokeAsync<PortalLoanDto?>(
            new GetPortalLoanQuery(tenantId, appId, actor), ct);
        return dto is null ? null : new PortalLoanView(
            dto.Id, dto.StatusLabel, dto.StatusStage,
            dto.StatusStages.Select(s => new PortalStageView(
                s.Key, s.Label, s.Done, s.Current)).ToList(),
            dto.Doc.ToJsonString(),
            dto.Documents.Select(d => new PortalDocItemView(
                d.DocId, d.PackageName, d.KindLabel, d.Ready,
                d.Filename, d.CompletedUtc)).ToList(),
            dto.Uploads.Select(u => new PortalUploadItemView(
                u.VaultDocId, u.Classification, u.StatusLabel, u.StatusKey,
                u.Filename, u.SizeBytes, u.UploadedUtc)).ToList());
    }

    public Task<IReadOnlyList<PortalLoanMeta>> PortalLoans(
        string tenantId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Borrower) return Task.FromResult<IReadOnlyList<PortalLoanMeta>>([]);
        return bus.InvokeAsync<IReadOnlyList<PortalLoanMeta>>(
            new ListPortalLoansQuery(tenantId, actor), ct);
    }

    /// Blind-index equality search (M11) — staff-only inside the
    /// handler; the value travels in the POST body, never a URL.
    public Task<ApplicationSearchResult> Search(
        string tenantId, string field, string value,
        IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        return bus.InvokeAsync<ApplicationSearchResult>(
            new SearchApplicationsQuery(tenantId, field, value, actor, scope), ct);
    }

    public Task<PricingEvalDto?> Pricing(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return Task.FromResult<PricingEvalDto?>(null);
        return bus.InvokeAsync<PricingEvalDto?>(
            new GetApplicationPricingQuery(tenantId, appId, actor, scope), ct);
    }

    public Task<IReadOnlyList<PricingEvalDto>> PricingHistory(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return Task.FromResult<IReadOnlyList<PricingEvalDto>>([]);
        return bus.InvokeAsync<IReadOnlyList<PricingEvalDto>>(
            new GetPricingHistoryQuery(tenantId, appId, actor, scope), ct);
    }

    public Task<FeeComparisonDto?> Fees(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return Task.FromResult<FeeComparisonDto?>(null);
        return bus.InvokeAsync<FeeComparisonDto?>(
            new GetApplicationFeesQuery(tenantId, appId, actor, scope), ct);
    }

    public Task<IReadOnlyList<FeeEvalDto>> FeeHistory(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return Task.FromResult<IReadOnlyList<FeeEvalDto>>([]);
        return bus.InvokeAsync<IReadOnlyList<FeeEvalDto>>(
            new GetFeeHistoryQuery(tenantId, appId, actor, scope), ct);
    }

    public Task<RateLockDto?> RateLock(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return Task.FromResult<RateLockDto?>(null);
        return bus.InvokeAsync<RateLockDto?>(
            new GetRateLockQuery(tenantId, appId, actor, scope), ct);
    }

    public Task<IReadOnlyList<DocDto>> Documents(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return Task.FromResult<IReadOnlyList<DocDto>>([]);
        return bus.InvokeAsync<IReadOnlyList<DocDto>>(
            new GetApplicationDocumentsQuery(tenantId, appId, actor, scope), ct);
    }

    /// TRID tolerance posture (M21 P3) — same staff-scoped handler as
    /// the REST GET; non-staff sees null like every other read here.
    public Task<Lola.Application.Compliance.ToleranceReportDto?> Tolerance(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff)
            return Task.FromResult<Lola.Application.Compliance.ToleranceReportDto?>(null);
        return bus.InvokeAsync<Lola.Application.Compliance.ToleranceReportDto?>(
            new Lola.Application.Compliance.GetApplicationToleranceQuery(
                tenantId, appId, actor, scope), ct);
    }

    /// ATR/QM battery posture (M22 P3) — same staff-scoped handler as
    /// the REST GET; non-staff sees null (ADR-0011).
    public Task<Lola.Application.Compliance.AtrQmReportDto?> Atr(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff)
            return Task.FromResult<Lola.Application.Compliance.AtrQmReportDto?>(null);
        return bus.InvokeAsync<Lola.Application.Compliance.AtrQmReportDto?>(
            new Lola.Application.Compliance.GetApplicationAtrQuery(
                tenantId, appId, actor, scope), ct);
    }

    public Task<IReadOnlyList<VaultDocDto>> Uploads(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return Task.FromResult<IReadOnlyList<VaultDocDto>>([]);
        return bus.InvokeAsync<IReadOnlyList<VaultDocDto>>(
            new GetApplicationUploadsQuery(tenantId, appId, actor, scope), ct);
    }

    public Task<IReadOnlyList<StepEventDto>> Steps(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return Task.FromResult<IReadOnlyList<StepEventDto>>([]);
        return bus.InvokeAsync<IReadOnlyList<StepEventDto>>(
            new GetApplicationStepsQuery(tenantId, appId, actor, scope), ct);
    }

    /// Mutation event spine (M15 P3) — staff-only; borrowers get null
    /// (the M12 lockdown pattern). Rows carry blind-index tokens for
    /// PII classes, never values.
    public async Task<IReadOnlyList<LoanEvent>?> LoanEvents(
        string tenantId, string appId, IResolverContext context,
        CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return null;
        return await bus.InvokeAsync<IReadOnlyList<LoanEvent>>(
            new Lola.Application.Events.GetLoanEventsQuery(tenantId,
                appId, actor, scope), ct);
    }

    /// Conditions detail (M15 P3) — staff-only; borrowers get null.
    /// The borrower projection rides REST /conditions.
    public async Task<IReadOnlyList<ConditionDto>?> Conditions(
        string tenantId, string appId, IResolverContext context,
        CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return null;
        return await bus.InvokeAsync<IReadOnlyList<ConditionDto>>(
            new Lola.Application.Events.ListConditionsQuery(tenantId,
                appId, actor, scope), ct);
    }

    /// Officer work queue (M16 P3) — staff-only; borrowers get null
    /// (the M12 lockdown pattern). Same lazy-eval ranking as REST
    /// /tasks.
    public async Task<IReadOnlyList<Lola.Application.Tasks.QueueTask>?>
        Tasks(string tenantId, IResolverContext context,
            CancellationToken ct, IReadOnlyList<string>? skills = null)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return null;
        return await bus.InvokeAsync<
            IReadOnlyList<Lola.Application.Tasks.QueueTask>>(
            new Lola.Application.Tasks.ListTasksQuery(tenantId, actor,
                scope, skills), ct);
    }

    /// Next-best-task (M16 P3) — staff-only; borrowers get null.
    public async Task<Lola.Application.Tasks.QueueTask?> NextTask(
        string tenantId, IResolverContext context,
        CancellationToken ct, IReadOnlyList<string>? skills = null)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return null;
        return await bus.InvokeAsync<Lola.Application.Tasks.QueueTask?>(
            new Lola.Application.Tasks.GetNextTaskQuery(tenantId, actor,
                scope, skills), ct);
    }

    /// Vendor order surface (MPE-229) — staff-only; borrowers get
    /// null (the M12 lockdown pattern). The same handler as REST
    /// /vendor-orders — summary reads are audited there.
    public async Task<IReadOnlyList<VendorOrderView>?> VendorOrders(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        if (scope != ActorScope.Staff) return null;
        var dtos = await bus.InvokeAsync<IReadOnlyList<VendorOrderDto>>(
            new ListVendorOrdersQuery(tenantId, appId, actor, scope), ct);
        return dtos.Select(d => new VendorOrderView(
            d.OrderId, d.ApplicationId, d.Kind, d.Adapter,
            d.ExternalId, d.Status, d.Error, d.OrderedBy,
            d.CreatedUtc, d.SubmittedUtc, d.CompletedUtc,
            d.Summary)).ToList();
    }

    public Task<IReadOnlyList<AuditEvent>> ApplicationAudit(
        string tenantId, string appId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<AuditEvent>>(
            new GetApplicationAuditQuery(tenantId, appId, actor, scope), ct);
    }

    public Task<IReadOnlyList<AuditEvent>> TenantAudit(
        string tenantId, IResolverContext context, CancellationToken ct,
        string? actor = null, string? action = null,
        string? since = null, string? until = null)
    {
        var (actorId, scope) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<AuditEvent>>(
            new GetTenantAuditQuery(tenantId, actorId, scope,
                actor, action, since, until), ct);
    }

    public Task<IReadOnlyList<GroupDto>> TenantGroups(
        string tenantId, IResolverContext context, CancellationToken ct)
    {
        var (_, scope) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<GroupDto>>(
            new ListGroupsQuery(tenantId, scope), ct);
    }

    /// Member ids of one group — the admin membership dialog joins
    /// them against the tenant user list (MPE-211).
    public Task<IReadOnlyList<string>> GroupMembers(
        string tenantId, string groupId, IResolverContext context,
        CancellationToken ct)
    {
        var (_, scope) = Actor(context);
        return bus.InvokeAsync<IReadOnlyList<string>>(
            new ListGroupMembersQuery(tenantId, groupId, scope), ct);
    }

    public Task<TenantRetentionReport> Retention(
        string tenantId, IResolverContext context, CancellationToken ct)
    {
        var (actor, scope) = Actor(context);
        return bus.InvokeAsync<TenantRetentionReport>(
            new GetTenantRetentionQuery(tenantId, actor, scope), ct);
    }

    private static (string ActorId, ActorScope Scope) Actor(IResolverContext context)
    {
        var user = context.GetUser() ?? new ClaimsPrincipal();
        return (user.FindFirst("sub")?.Value ?? "unknown",
            LolaPolicies.ScopeFor(user));
    }
}

/// GraphQL-facing application view — the payload stays an opaque
/// document string (masked upstream by DocMasker).
public sealed record ApplicationView(
    string Id, string Status, string PipelineStep, string? BorrowerUserId,
    int CurrentVersion, string? TridClockStart, string? TridDeadlineUtc,
    string? SubmittedUtc, string DocJson);

/// GraphQL-facing portal views — flat records mirror the app DTOs so
/// Hot Chocolate can bind them (records are already schema-friendly;
/// the payload doc stays an opaque masked string).
public sealed record PortalStageView(
    string Key, string Label, bool Done, bool Current);
public sealed record PortalDocItemView(
    string DocId, string PackageName, string KindLabel,
    bool Ready, string? Filename, string? CompletedUtc);
public sealed record PortalUploadItemView(
    string VaultDocId, string Classification, string StatusLabel,
    string StatusKey, string Filename, long SizeBytes, string? UploadedUtc);
public sealed record PortalLoanView(
    string Id, string StatusLabel, int StatusStage,
    IReadOnlyList<PortalStageView> StatusStages,
    string DocJson,
    IReadOnlyList<PortalDocItemView> Documents,
    IReadOnlyList<PortalUploadItemView> Uploads);

/// GraphQL-facing vendor order view — scalar fields only; the
/// encrypted request/result documents never reach the schema (the
/// audited detail REST read is the only decrypt path).
public sealed record VendorOrderView(
    string OrderId, string ApplicationId, string Kind,
    string Adapter, string? ExternalId, string Status,
    string? Error, string OrderedBy, string? CreatedUtc,
    string? SubmittedUtc, string? CompletedUtc, string? Summary);
