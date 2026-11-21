using Lola.Application.Admin;
using Lola.Application.Identity;
using Lola.Domain.Applications;
using Lola.Domain.Classification;
using Lola.Domain.Identity;

namespace Lola.Application.Applications;

/// Metas only — no payload — so masking can't leak through a doc.
/// Borrower actors see their own drafts only. The handler owns the
/// visibility boundary: the repo returns party[0]'s name plaintext and
/// it is masked here unless the caller's role grants the Name class
/// (ADR-0008); the TRID deadline derives from the clock stamp.
public sealed record ListApplicationsQuery(
    string TenantId, string ActorId, ActorScope Scope);

public static class ListApplicationsHandler
{
    public static async Task<IReadOnlyList<ApplicationMeta>> Handle(
        ListApplicationsQuery q, IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var all = await apps.ListAsync(ct);
        var canSeeName = VisibilityContext.Current.Contains(PiiClass.Name);
        var enriched = all.Select(a => a with
        {
            BorrowerName = a.BorrowerName is null || canSeeName
                ? a.BorrowerName
                : DocMasker.Mask(a.BorrowerName),
            TridDeadlineUtc = TridClock.DeadlineUtc(a.TridClockStart)
        }).ToList();
        // Linked-only visibility (M19): a partner sees exactly the
        // apps an active application_partners row names — nothing
        // else in the tenant.
        if (q.Scope == ActorScope.Partner)
        {
            var linked = await apps.LinkedAppIdsForPartnerAsync(q.ActorId, ct);
            return enriched.Where(a => linked.Contains(a.Id)).ToList();
        }
        return q.Scope == ActorScope.Borrower
            ? enriched.Where(a => a.BorrowerUserId == q.ActorId).ToList()
            : enriched;
    }
}
