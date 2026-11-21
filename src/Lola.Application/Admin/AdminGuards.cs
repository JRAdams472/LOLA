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

    /// Groups confer human authority only (ADR-0005, MPE-271): Service
    /// is machine authority minted by the token endpoint — a group that
    /// could grant it would let a TenantAdmin widen any member into
    /// Service-scoped surfaces (staff application scope, any-tenant
    /// status reads). Enum.IsDefined also rejects numeric members that
    /// TryParse would smuggle through as undefined values.
    public static void RequireAssignableRoles(IReadOnlyList<LolaRole> roles)
    {
        foreach (var role in roles)
            if (!Enum.IsDefined(role) || role == LolaRole.Service)
                throw DomainException.BadRequest(
                    $"role '{role}' is not assignable to a group");
    }

    /// The AdminRead decision enforced in-handler (MPE-272): REST's
    /// AdminRead policy and the GraphQL resolvers share exactly this
    /// check, so a directory read can never rely on masking alone.
    /// Staff scope == the AdminRead role set (TenantAdmin/LoanOfficer/
    /// Service) via LolaPolicies.ScopeFor.
    public static void RequireStaffScope(Applications.ActorScope scope)
    {
        if (scope != Applications.ActorScope.Staff)
            throw DomainException.Forbidden(
                "directory reads require staff scope");
    }
}
