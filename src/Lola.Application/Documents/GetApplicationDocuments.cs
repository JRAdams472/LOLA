using Lola.Application.Admin;
using Lola.Application.Applications;

namespace Lola.Application.Documents;

/// GET …/applications/{id}/documents (MPE-190) — the package
/// registry for one application, newest-first by insertion. Staff
/// see all docs on the app; borrowers reach only their own
/// application's docs (RequireAppAsync enforces own-draft scoping —
/// the registry itself holds no PII, only references).
public sealed record GetApplicationDocumentsQuery(
    string TenantId, string AppId, string ActorId,
    bool ActorIsBorrower);

public static class GetApplicationDocumentsHandler
{
    public static async Task<IReadOnlyList<DocDto>> Handle(
        GetApplicationDocumentsQuery q, IApplicationRepository apps,
        IDocumentRepository docs, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.ActorIsBorrower, ct);
        var rows = await docs.ListDocsAsync(q.AppId, ct);
        return rows.Select(DocWire.ToDto).ToList();
    }
}
