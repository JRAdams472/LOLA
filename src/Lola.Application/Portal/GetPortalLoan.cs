using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Documents;
using Lola.Application.Vault;

namespace Lola.Application.Portal;

/// The borrower portal's composite read (M12): own application only,
/// borrower vocabulary only. Deliberately NOT a reuse of
/// GetApplicationQuery — ApplicationDto carries pipeline_step,
/// versions, actor ids, TRID internals, and lock data that the
/// borrower must never see (ticket acceptance). RequireAppAsync
/// enforces own-app scoping; DocMasker runs under the borrower
/// visibility context (zero plaintext grants → masked-confirm).
public sealed record GetPortalLoanQuery(
    string TenantId, string AppId, string ActorId);

public static class GetPortalLoanHandler
{
    public static async Task<PortalLoanDto?> Handle(
        GetPortalLoanQuery q, IApplicationRepository apps,
        IDocumentRepository docs, IVaultDocRepository vault,
        Audit.ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, isBorrower: true, ct);
        await audit.WriteAsync(q.ActorId, "read", "application", q.AppId,
            Audit.AuditFields.InDoc(app.Doc), "portal read", ct);
        DocMasker.MaskInPlace(app.Doc);

        var (label, stage) = BorrowerStatusMap.Map(app.Status);
        var documents = (await docs.ListDocsAsync(q.AppId, ct))
            .Select(d =>
            {
                var (dl, ready) = BorrowerStatusMap.DocumentStatus(d.Status);
                return new PortalDocItem(d.DocId, d.PackageName,
                    BorrowerStatusMap.DocumentKindLabel(d.Kind),
                    ready, d.Filename, d.CompletedUtc);
            }).ToList();
        var uploads = (await vault.ListAsync(q.AppId, ct))
            .Select(u =>
            {
                var (key, ul) = BorrowerStatusMap.UploadStatus(u.Status);
                return new PortalUploadItem(u.VaultDocId, u.Classification,
                    ul, key, u.Filename, u.SizeBytes, u.CreatedUtc);
            }).ToList();

        return new PortalLoanDto(
            app.Id, label, stage, BorrowerStatusMap.StageList(stage),
            app.Doc.CloneRoot(), documents, uploads);
    }
}
