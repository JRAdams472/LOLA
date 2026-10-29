using Lola.Application.Admin;
using Lola.Application.Errors;

namespace Lola.Application.Applications;

/// Blind-index equality search — officers/staff only; a borrower
/// searching by SSN across other drafts would violate the own-draft
/// boundary, so the handler forbids it outright.
public sealed record SearchApplicationsQuery(
    string TenantId, string Field, string Value, string ActorId,
    bool ActorIsBorrower);

public sealed record ApplicationSearchResult(IReadOnlyList<string> ApplicationIds);

public static class SearchApplicationsHandler
{
    public static async Task<ApplicationSearchResult> Handle(
        SearchApplicationsQuery q, IApplicationPartyRepository parties,
        Audit.ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden("blind search is staff-only");
        var ids = await parties.SearchAsync(q.Field, q.Value, ct);
        await audit.WriteAsync(q.ActorId, "search", "application", null,
            Audit.AuditFields.ForPath(q.Field),
            $"blind search ({ids.Count} hits)", ct);
        return new ApplicationSearchResult(ids);
    }
}
