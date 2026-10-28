using Lola.Application.Errors;
using Lola.Application.Identity;

namespace Lola.Application.Admin.Tenants;

public sealed record BackupTenantCommand(string TenantId);

/// Snapshot one tenant's data-plane file (MPE-95): resolves the
/// registry row so the job surface gets the TenantRecord it needs
/// (DbPath + wrapped key) — unscoped operator surface, Provision-only
/// at the edge.
public static class BackupTenantHandler
{
    public static async Task<TenantBackupInfo> Handle(
        BackupTenantCommand c, ITenantRegistry registry, ITenantBackup backup,
        CancellationToken ct)
    {
        var tenant = await registry.FindByIdAsync(c.TenantId, ct)
            ?? throw DomainException.NotFound("tenant not found");
        return await backup.BackupAsync(tenant, ct);
    }
}
