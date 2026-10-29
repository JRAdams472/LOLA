using Lola.Application.Admin;
using Lola.Domain.Applications;
using Lola.Domain.Identity;

namespace Lola.Application.Applications;

/// Metas only — no payload — so masking can't leak through a doc.
/// Borrower actors see their own drafts only.
public sealed record ListApplicationsQuery(
    string TenantId, string ActorId, bool ActorIsBorrower);

public static class ListApplicationsHandler
{
    public static async Task<IReadOnlyList<ApplicationMeta>> Handle(
        ListApplicationsQuery q, IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var all = await apps.ListAsync(ct);
        return q.ActorIsBorrower
            ? all.Where(a => a.BorrowerUserId == q.ActorId).ToList()
            : all;
    }
}
