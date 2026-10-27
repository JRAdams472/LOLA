using Lola.Application.Errors;
using Lola.Application.Identity;

namespace Lola.Application.Admin.Tenants;

public sealed record GetTenantQuery(string TenantId);

/// Tenant status view — scope is enforced at the edge (own-tenant for
/// user tokens, any for Service), so the handler is a plain lookup
/// (MPE-87).
public static class GetTenantHandler
{
    public static async Task<TenantAdminDto> Handle(
        GetTenantQuery q, ITenantRegistry registry, CancellationToken ct)
    {
        var t = await registry.FindByIdAsync(q.TenantId, ct)
            ?? throw DomainException.NotFound("tenant not found");
        return new TenantAdminDto(t.Id, t.Slug, t.DisplayName, t.Status, t.CreatedUtc);
    }
}
