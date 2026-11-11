using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Tenancy;

namespace Lola.Application.Vault;

/// Signed-URL TTL for vault downloads — default 15 minutes, the same
/// presigned lifetime the doc-package download uses (MPE-202).
public sealed record VaultUrlOptions(int TtlSeconds = 900);

/// GET …/uploads/{docId}/download — mints a ≤15-min capability URL for
/// the clean object and 302s to it. The mint is the audited,
/// authorized step; the capability route it points at is auth-free by
/// design (the signature IS the credential).
public sealed record GetVaultDownloadQuery(
    string TenantId, string AppId, string VaultDocId, string ActorId,
    bool ActorIsBorrower);

public sealed record VaultDownloadResult(string Url);

public static class GetVaultDownloadHandler
{
    public static async Task<VaultDownloadResult> Handle(
        GetVaultDownloadQuery q, IApplicationRepository apps,
        IVaultDocRepository vault, IVaultUrlSigner signer,
        ITenantAuditLog audit, VaultUrlOptions options,
        TimeProvider clock, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var doc = await vault.GetAsync(q.VaultDocId, ct);
        if (doc is null || doc.ApplicationId != q.AppId)
            throw DomainException.NotFound("upload not found");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, doc.ApplicationId, q.ActorId, q.ActorIsBorrower, ct);
        if (doc.Status != "clean")
            throw new DomainException(409,
                $"upload is '{doc.Status}' — only clean uploads " +
                "may be downloaded");

        var exp = clock.GetUtcNow().AddSeconds(options.TtlSeconds);
        var sig = await signer.SignAsync(doc.VaultDocId, exp, ct);
        var url = $"/api/vault-content/{doc.VaultDocId}" +
            $"?tid={Uri.EscapeDataString(q.TenantId)}" +
            $"&exp={exp.ToUnixTimeSeconds()}" +
            $"&sig={Uri.EscapeDataString(sig)}";

        // Audit the mint, not the fetch — a capability minted is an
        // access granted. Filename stays out (PII-class, invariant 3);
        // the doc id + app id are reference-only attribution.
        await audit.WriteAsync(q.ActorId, "vault_download",
            "application", q.AppId, ["vault_docs"],
            $"download minted: {doc.VaultDocId}", ct);
        return new VaultDownloadResult(url);
    }
}
