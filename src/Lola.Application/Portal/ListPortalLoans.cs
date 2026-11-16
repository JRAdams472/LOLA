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
        var all = await apps.ListAsync(ct);
        return all
            .Where(a => a.BorrowerUserId == q.ActorId)
            .Select(a =>
            {
                var (label, stage) = BorrowerStatusMap.Map(a.Status);
                return new PortalLoanMeta(a.Id, label, stage);
            })
            .ToList();
    }
}
