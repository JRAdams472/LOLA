using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;

namespace Lola.Application.Documents;

/// GET …/documents/{docId}/download (MPE-190) — mints a fresh
/// presigned URL for a completed package through the adapter and
/// audits the access (`doc_download`). The endpoint 302s to it —
/// LOLA never proxies package bytes. Staff or borrower-own only.
public sealed record GetDocumentDownloadQuery(
    string TenantId, string AppId, string DocId, string ActorId,
    bool ActorIsBorrower);

/// The redirect target the endpoint turns into a 302.
public sealed record DocDownloadResult(string Url);

public static class GetDocumentDownloadHandler
{
    public static async Task<DocDownloadResult> Handle(
        GetDocumentDownloadQuery q, IApplicationRepository apps,
        IDocumentRepository docs, IDocumentService adapter,
        ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var doc = await docs.GetDocAsync(q.DocId, ct);
        // A doc id from another application is a 404, never a leak.
        if (doc is null || doc.ApplicationId != q.AppId)
            throw Errors.DomainException.NotFound(
                "document not found");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, doc.ApplicationId, q.ActorId, q.ActorIsBorrower, ct);
        if (doc.Status != "completed" || doc.RequestId is null)
            throw Errors.DomainException.Conflict(
                "document package is not completed yet");
        var url = await adapter.GetDownloadUrlAsync(doc.RequestId, ct);
        // Entity is the application — the access-review report
        // filters on it, and a document read is a PII access on the
        // app. The doc id rides in the reason, never field values.
        await audit.WriteAsync(q.ActorId, "doc_download",
            "application", doc.ApplicationId, ["documents"],
            $"presigned download issued for {doc.DocId}", ct);
        return new DocDownloadResult(url);
    }
}
