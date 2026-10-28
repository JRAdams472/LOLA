using Lola.Application.Errors;
using Lola.Application.Identity;

namespace Lola.Application.Admin.Tenants;

public sealed record ListTenantBackupsQuery(string TenantId);

/// Snapshot manifest listing for one tenant (MPE-95) — the registry
/// lookup keeps a typo'd tenant id honest 404 instead of an empty list.
public static class ListTenantBackupsHandler
{
    public static async Task<IReadOnlyList<TenantBackupInfo>> Handle(
        ListTenantBackupsQuery q, ITenantRegistry registry, ITenantBackup backup,
        CancellationToken ct)
    {
        _ = await registry.FindByIdAsync(q.TenantId, ct)
            ?? throw DomainException.NotFound("tenant not found");
        return await backup.ListAsync(q.TenantId, ct);
    }
}
