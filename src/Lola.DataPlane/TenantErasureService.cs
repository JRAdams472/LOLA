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
        await keys.DestroyAsync(tenant.Id, ct);

        var removed = 0;
        foreach (var path in new[]
        {
            tenant.DbPath, tenant.DbPath + "-wal", tenant.DbPath + "-shm"
        })
        {
            if (!File.Exists(path)) continue;
            File.Delete(path);
            removed++;
        }

        var backups = Path.Combine(backupDir, tenant.Id);
        if (Directory.Exists(backups))
        {
            removed += Directory.EnumerateFiles(backups).Count();
            Directory.Delete(backups, recursive: true);
        }

        await registry.SetStatusAsync(tenant.Id, "retired", ct);
        return new TenantErasureResult(tenant.Id, removed);
    }
}
