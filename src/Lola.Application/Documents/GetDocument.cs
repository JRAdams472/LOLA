using Lola.Application.Admin;
using Lola.Application.Applications;

namespace Lola.Application.Documents;

/// GET …/applications/{id}/documents/{docId} (MPE-190) — one
/// registry row. A doc id from another application 404s rather than
/// revealing it exists; borrowers reach only their own app's docs.
public sealed record GetDocumentQuery(
    string TenantId, string AppId, string DocId, string ActorId,
    bool ActorIsBorrower);

public static class GetDocumentHandler
{
    public static async Task<DocDto> Handle(
        GetDocumentQuery q, IApplicationRepository apps,
        IDocumentRepository docs, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var doc = await docs.GetDocAsync(q.DocId, ct);
        if (doc is null || doc.ApplicationId != q.AppId)
            throw Errors.DomainException.NotFound(
                "document not found");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, doc.ApplicationId, q.ActorId, q.ActorIsBorrower, ct);
        return DocWire.ToDto(doc);
    }
}
