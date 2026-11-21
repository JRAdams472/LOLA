using Lola.Application.Admin;
using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Domain.Applications;
using Lola.Domain.Classification;
using Lola.Domain.Identity;

namespace Lola.Application.Applications;

/// Metas only — no payload — so masking can't leak through a doc.
/// External actors get a SQL-scoped page (MPE-266): the owner/link
/// predicate runs before any name ciphertext loads; a scope that
/// can't see names skips the decrypt entirely and carries the opaque
/// masked label instead. Page size is bounded; order is
/// (created_utc DESC, id) — deterministic across pages.
public sealed record ListApplicationsQuery(
    string TenantId, string ActorId, ActorScope Scope,
    int Page = 1, int PageSize = 50);

public sealed record ApplicationListPage(
    IReadOnlyList<ApplicationMeta> Items, int Total,
    int Page, int PageSize);

public static class ListApplicationsHandler
{
    public const int MaxPageSize = 100;

    public static async Task<ApplicationListPage> Handle(
        ListApplicationsQuery q, IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Page < 1 || q.PageSize < 1 || q.PageSize > MaxPageSize)
            throw DomainException.BadRequest(
                $"page >= 1 and 1 <= pageSize <= {MaxPageSize} required");
        var canSeeName = VisibilityContext.Current.Contains(PiiClass.Name);
        var page = await apps.ListScopedAsync(q.Scope, q.ActorId,
            (q.Page - 1) * q.PageSize, q.PageSize, canSeeName, ct);
        var items = page.Items.Select(a => a with
        {
            TridDeadlineUtc = TridClock.DeadlineUtc(a.TridClockStart)
        }).ToList();
        return new ApplicationListPage(items, page.Total,
            q.Page, q.PageSize);
    }
}
