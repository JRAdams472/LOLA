using Lola.Application.Identity;

namespace Lola.Application.Admin.Tenants;

public sealed record GetTenantsQuery;

public sealed record TenantAdminDto(
    string Id, string Slug, string DisplayName, string Status, string CreatedUtc);

/// Global tenant registry view — TenantAdmin-only at the edge (MPE-86).
public static class GetTenantsHandler
{
    public static async Task<IReadOnlyList<TenantAdminDto>> Handle(
        GetTenantsQuery _, ITenantRegistry registry, CancellationToken ct)
        => (await registry.ListAsync(ct))
            .Select(t => new TenantAdminDto(t.Id, t.Slug, t.DisplayName, t.Status, t.CreatedUtc))
            .ToList();
}
