using Lola.Application.Admin;
using Lola.Application.Applications;

namespace Lola.Application.Portal;

/// Dashboard list for the portal — the borrower's own applications as
/// status metas (no payload doc). Same own-app boundary as
/// ListApplicationsQuery but returns borrower vocabulary only.
public sealed record ListPortalLoansQuery(string TenantId, string ActorId);

public static class ListPortalLoansHandler
{
    public static async Task<IReadOnlyList<PortalLoanMeta>> Handle(
        ListPortalLoansQuery q, IApplicationRepository apps,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        // Scoped SQL page (MPE-266): the borrower predicate runs in the
        // query — unrelated tenants' rows and name ciphertexts are
        // never loaded for a portal list. Borrowers have a handful of
        // apps; the cap is a floor, not pagination.
        var page = await apps.ListScopedAsync(ActorScope.Borrower,
            q.ActorId, 0, 100, includeNames: false, ct);
        return page.Items
            .Select(a =>
            {
                var (label, stage) = BorrowerStatusMap.Map(a.Status);
                return new PortalLoanMeta(a.Id, label, stage);
            })
            .ToList();
    }
}
