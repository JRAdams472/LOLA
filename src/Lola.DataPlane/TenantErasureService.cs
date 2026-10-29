using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Identity;

namespace Lola.DataPlane;

/// Crypto-shredding erasure (MPE-96, invariant 9): the tenant's transit
/// keys are destroyed BEFORE the files are deleted — a crash between
/// steps leaves ciphertext nobody can ever unwrap, not plaintext-ish
/// material on disk. Once keys die, file deletion is cleanup, not the
/// security boundary. Destructive and irreversible; the caller gates
/// retention (M6 adds the Reg B 25-month check).
public sealed class TenantErasureService(
    ITenantKeyManager keys,
    ITenantRegistry registry,
    IControlPlaneResiduals residuals,
    string backupDir = "data/backups") : ITenantEraser
{
    public async Task<TenantErasureResult> EraseAsync(
        TenantRecord tenant, CancellationToken ct = default)
    {
        // Guard on LIVE status — a caller's TenantRecord can be a stale
        // snapshot taken before an earlier erasure; the registry is
        // authoritative.
        var live = await registry.FindByIdAsync(tenant.Id, ct)
            ?? throw new InvalidOperationException(
                $"Tenant '{tenant.Id}' is not in the registry.");
        if (live.Status == "retired")
            throw new InvalidOperationException(
                $"Tenant '{tenant.Id}' is already retired — nothing to erase.");

        // KEYS FIRST — invariant 9. After this both transit keys are
        // two-step deleted; every ciphertext, blind index, and wrapped
        // file key for the tenant is permanently unrecoverable.
        // DestroyAsync is idempotent (MPE-107): a retried erase whose
        // keys already died reads them as absent, not an error.
        await keys.DestroyAsync(tenant.Id, ct);

        // Cleanup is best-effort: a locked or missing file must not
        // wedge the lifecycle — once keys are dead the bytes are
        // unreadable regardless, so converge to `retired` and report
        // whatever didn't delete for the operator to sweep.
        var removed = 0;
        var unresolved = new List<string>();
        foreach (var path in new[]
        {
            tenant.DbPath, tenant.DbPath + "-wal", tenant.DbPath + "-shm"
        })
        {
            if (!File.Exists(path)) continue;
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException) { unresolved.Add(path); }
            catch (UnauthorizedAccessException) { unresolved.Add(path); }
        }

        var backups = Path.Combine(backupDir, tenant.Id);
        if (Directory.Exists(backups))
        {
            try
            {
                removed += Directory.EnumerateFiles(backups).Count();
                Directory.Delete(backups, recursive: true);
            }
            catch (IOException) { unresolved.Add(backups); }
            catch (UnauthorizedAccessException) { unresolved.Add(backups); }
        }

        // Control-plane residuals (MPE-108/119): identity rows live
        // OUTSIDE the shredded file, so keys dying doesn't touch them —
        // they are deleted/anonymized here, before `retired` flips.
        // Ordering matters: `retired` is the terminal convergence flag,
        // so it is written only after every destructive step succeeded.
        // A residuals failure leaves the tenant non-retired and this
        // whole erase retryable; the cleanup itself is idempotent.
        await residuals.CleanAsync(tenant.Id, ct);

        await registry.SetStatusAsync(tenant.Id, "retired", ct);
        return new TenantErasureResult(tenant.Id, removed, unresolved);
    }
}
