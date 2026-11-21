using Lola.Application.Admin;
using Lola.Application.Applications;

namespace Lola.Application.Vault;

/// GET …/uploads (MPE-200) — the app's vault registry, newest first.
/// Staff or borrower-own only. Filenames come back decrypted — the
/// callers (officer, the borrower who uploaded) are authorized PII
/// viewers; the access itself is what the audit spine tracks.
public sealed record GetApplicationUploadsQuery(
    string TenantId, string AppId, string ActorId,
    ActorScope Scope);

public static class GetApplicationUploadsHandler
{
    public static async Task<IReadOnlyList<VaultDocDto>> Handle(
        GetApplicationUploadsQuery q, IApplicationRepository apps,
        IVaultDocRepository vault, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.Scope, ct);
        var staff = q.Scope == ActorScope.Staff;
        var docs = await vault.ListAsync(q.AppId, ct);
        // Own-uploads-only (M19): a partner sees the uploads they
        // pushed, never the borrower's or staff's.
        if (q.Scope == ActorScope.Partner)
            docs = docs.Where(d => d.Actor == q.ActorId).ToList();
        return docs.Select(d => ToDto(d, staff)).ToList();
    }

    internal static VaultDocDto ToDto(VaultDocRecord d,
        bool includeIntel = false) => new(
        d.VaultDocId, d.ApplicationId, d.Classification, d.Filename,
        d.ContentType, d.SizeBytes, d.Sha256, d.Status, d.Error,
        d.CreatedUtc, d.ScannedUtc,
        includeIntel
            ? DocIntel.Of(d.ClassificationJson, d.ClassifiedUtc)
            : null);
}
