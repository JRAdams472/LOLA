using Lola.Application.Errors;
using Lola.Application.Identity;

namespace Lola.Application.Admin.Tenants;

public sealed record RestoreTenantBackupCommand(string TenantId, string Name);

/// Restore one tenant's data-plane file from a named snapshot (MPE-95).
/// The manifest listing gates the name — unknown (or traversal-shaped)
/// names are 404 before the service ever touches the filesystem.
public static class RestoreTenantBackupHandler
{
    public static async Task<TenantBackupInfo> Handle(
        RestoreTenantBackupCommand c, ITenantRegistry registry,
        ITenantBackup backup, CancellationToken ct)
    {
        var tenant = await registry.FindByIdAsync(c.TenantId, ct)
            ?? throw DomainException.NotFound("tenant not found");
        var known = await backup.ListAsync(c.TenantId, ct);
        if (known.All(b => b.Name != c.Name))
            throw DomainException.NotFound("backup not found");
        return await backup.RestoreAsync(tenant, c.Name, ct);
    }
}
