using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;

namespace Lola.Application.Vault;

/// POST …/uploads (MPE-200) — streams a borrower document through
/// the sealing cipher into the tenant's quarantine prefix, registers
/// it, and enqueues the AV scan — all off the request path after the
/// 202. `Content` is the raw multipart section body; plaintext only
/// ever exists in streams (never buffered, never a temp file —
/// invariant 1). Staff or borrower-own only.
public sealed record UploadVaultDocCommand(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower,
    string Classification, string? DeclaredFileName,
    string? DeclaredContentType, Stream Content);

/// 202 result the endpoint maps.
public sealed record VaultUploadAccepted(
    string VaultDocId, string Status, bool Deduplicated);

/// Runtime upload bounds — VAULT_MAX_BYTES env (default 25 MiB).
public sealed record VaultUploadOptions(long MaxBytes = 25L << 20);

public static class UploadVaultDocHandler
{
    public static async Task<VaultUploadAccepted> Handle(
        UploadVaultDocCommand c, IApplicationRepository apps,
        IVaultDocRepository vault, IObjectVault store,
        VaultCipher cipher, ITenantAuditLog audit,
        VaultUploadOptions options, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, c.ActorIsBorrower, ct);

        // Sniff the head before any of it is sealed — magic bytes
        // decide the stored type; the declared name/type are hints.
        var head = new byte[UploadAllowlist.SniffBytes];
        var headLen = await ReadHeadAsync(c.Content, head, ct);
        var contentType = UploadAllowlist.Validate(
            c.Classification, c.DeclaredFileName,
            c.DeclaredContentType, head.AsSpan(0, headLen));

        var docId = $"vlt-{Guid.NewGuid():N}";
        var key = $"quarantine/{docId}";
        await using var sealing = cipher.Seal(
            new PrefixStream(head[..headLen], c.Content),
            options.MaxBytes);
        try
        {
            await store.PutAsync(key, sealing, ct);
        }
        catch (VaultTooLargeException)
        {
            // A partial object must never sit in quarantine — best
            // effort; the row was never written so nothing points here.
            try { await store.DeleteAsync(key, ct); } catch { /* best effort */ }
            throw new DomainException(413,
                $"upload exceeds the {options.MaxBytes}-byte limit");
        }
        catch
        {
            try { await store.DeleteAsync(key, ct); } catch { /* best effort */ }
            throw;
        }

        var requestHash =
            $"up:{sealing.Sha256Hex}:{c.AppId}:{c.Classification.ToLowerInvariant()}";
        var record = new VaultDocRecord(
            docId, c.AppId, c.Classification.ToLowerInvariant(),
            c.DeclaredFileName ?? "upload", contentType,
            sealing.PlaintextLength, sealing.Sha256Hex,
            sealing.WrappedDek, store.Adapter, key,
            "quarantined", null, c.ActorId,
            "", null, requestHash);
        var result = await vault.EnqueueAsync(
            record, $"vso-{Guid.NewGuid():N}", ct);
        if (result.Deduplicated)
        {
            // The replay's fresh object is orphaned — the registry
            // still points at the original doc's quarantine key.
            try { await store.DeleteAsync(key, ct); }
            catch { /* best effort — ciphertext, not a leak */ }
        }

        // Filename never enters audit — the doc id + class are enough
        // to attribute the access (invariant 3).
        await audit.WriteAsync(c.ActorId, "vault_upload",
            "application", c.AppId, ["vault_docs"],
            $"upload accepted as {result.VaultDocId}", ct);
        return new VaultUploadAccepted(
            result.VaultDocId, "quarantined", result.Deduplicated);
    }

    private static async Task<int> ReadHeadAsync(
        Stream s, byte[] buf, CancellationToken ct)
    {
        var got = 0;
        while (got < buf.Length)
        {
            var n = await s.ReadAsync(
                buf.AsMemory(got, buf.Length - got), ct);
            if (n == 0) break;
            got += n;
        }
        return got;
    }
}
