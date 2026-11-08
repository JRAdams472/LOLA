using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;

namespace Lola.Application.Vault;

/// GET …/uploads/{docId} (MPE-200) — vault registry detail. A doc id
/// from another application is a 404, never a leak. Staff or
/// borrower-own only.
public sealed record GetVaultDocQuery(
    string TenantId, string AppId, string VaultDocId, string ActorId,
    bool ActorIsBorrower);

public static class GetVaultDocHandler
{
    public static async Task<VaultDocDto> Handle(
        GetVaultDocQuery q, IApplicationRepository apps,
        IVaultDocRepository vault, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var doc = await vault.GetAsync(q.VaultDocId, ct);
        if (doc is null || doc.ApplicationId != q.AppId)
            throw DomainException.NotFound("upload not found");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, doc.ApplicationId, q.ActorId, q.ActorIsBorrower, ct);
        return GetApplicationUploadsHandler.ToDto(doc);
    }
}
