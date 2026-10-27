using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Domain.Identity;

namespace Lola.Application.Admin;

/// Shared tenant-scope guards for admin handlers (MPE-86). Every
/// tenant-scoped handler re-verifies the resolved tenant — a crafted
/// {id} arg can never reach another tenant's rows (ADR-0003). Group
/// lookups return 404 on cross-tenant ids so existence doesn't leak.
internal static class AdminGuards
{
    public static void RequireTenant(string tenantId)
    {
        if (TenantContext.Current?.TenantId != tenantId)
            throw DomainException.Forbidden("request tenant does not match the resolved tenant");
    }

    public static async Task<LolaGroup> RequireGroup(
        IGroupDirectory groups, string tenantId, string groupId, CancellationToken ct)
    {
        var group = await groups.FindByIdAsync(groupId, ct);
        if (group is null || group.TenantId != tenantId)
            throw DomainException.NotFound("group not found");
        return group;
    }
}
