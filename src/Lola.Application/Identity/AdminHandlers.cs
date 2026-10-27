using Lola.Application.Errors;
using Lola.Application.Tenancy;
using Lola.Domain.Identity;

namespace Lola.Application.Identity;

/// Admin-plane handlers (MPE-86). Every tenant-scoped operation
/// re-verifies the resolved tenant — a crafted {id} arg can never
/// reach another tenant's rows (ADR-0003). Group lookups return 404
/// on cross-tenant ids so existence doesn't leak across tenants.
public static class AdminHandlers
{
    private static void RequireTenant(string tenantId)
    {
        if (TenantContext.Current?.TenantId != tenantId)
            throw DomainException.Forbidden("request tenant does not match the resolved tenant");
    }

    private static async Task<LolaGroup> RequireGroup(
        IGroupDirectory groups, string tenantId, string groupId, CancellationToken ct)
    {
        var group = await groups.FindByIdAsync(groupId, ct);
        if (group is null || group.TenantId != tenantId)
            throw DomainException.NotFound("group not found");
        return group;
    }

    public static async Task<IReadOnlyList<TenantAdminDto>> Handle(
        GetTenantsQuery _, ITenantRegistry registry, CancellationToken ct)
        => (await registry.ListAsync(ct))
            .Select(t => new TenantAdminDto(t.Id, t.Slug, t.DisplayName, t.Status, t.CreatedUtc))
            .ToList();

    public static async Task<IReadOnlyList<AdminUserDto>> Handle(
        GetTenantUsersQuery q, IUserDirectory users, CancellationToken ct)
    {
        RequireTenant(q.TenantId);
        var result = new List<AdminUserDto>();
        foreach (var u in await users.ListByTenantAsync(q.TenantId, ct))
        {
            var roles = await users.RolesForUserAsync(u.Id, ct);
            result.Add(new AdminUserDto(u.Id, u.Email, u.DisplayName,
                u.Status.ToString().ToLowerInvariant(),
                roles.Select(r => r.ToString()).ToList(), u.MfaSecretRef));
        }
        return result;
    }

    public static async Task<IReadOnlyList<GroupDto>> Handle(
        ListGroupsQuery q, IGroupDirectory groups, CancellationToken ct)
    {
        RequireTenant(q.TenantId);
        var result = new List<GroupDto>();
        foreach (var g in await groups.ListByTenantAsync(q.TenantId, ct))
        {
            result.Add(new GroupDto(g.Id, g.Name, g.Description,
                g.Roles.Select(r => r.ToString()).ToList(),
                await groups.MemberCountAsync(g.Id, ct)));
        }
        return result;
    }

    public static async Task<GroupDto> Handle(
        CreateGroupCommand c, IGroupDirectory groups, CancellationToken ct)
    {
        RequireTenant(c.TenantId);
        var group = new LolaGroup($"g-{Guid.NewGuid():N}", c.TenantId,
            c.Name, c.Description, c.Roles);
        await groups.InsertAsync(group, ct);
        return new GroupDto(group.Id, group.Name, group.Description,
            c.Roles.Select(r => r.ToString()).ToList(), 0);
    }

    public static async Task<GroupDto> Handle(
        UpdateGroupCommand c, IGroupDirectory groups, CancellationToken ct)
    {
        RequireTenant(c.TenantId);
        var group = await RequireGroup(groups, c.TenantId, c.GroupId, ct);
        if (c.Name is not null || c.Description is not null)
            await groups.UpdateAsync(c.GroupId, c.Name, c.Description, ct);
        if (c.Roles is not null)
            await groups.SetRolesAsync(c.GroupId, c.Roles, ct);
        var updated = (await groups.FindByIdAsync(c.GroupId, ct))!;
        return new GroupDto(updated.Id, updated.Name, updated.Description,
            updated.Roles.Select(r => r.ToString()).ToList(),
            await groups.MemberCountAsync(updated.Id, ct));
    }

    public static async Task<GroupDto> Handle(
        DeleteGroupCommand c, IGroupDirectory groups, CancellationToken ct)
    {
        RequireTenant(c.TenantId);
        var group = await RequireGroup(groups, c.TenantId, c.GroupId, ct);
        await groups.DeleteAsync(group.Id, ct);
        return new GroupDto(group.Id, group.Name, group.Description,
            group.Roles.Select(r => r.ToString()).ToList(), 0);
    }

    public static async Task<GroupDto> Handle(
        AddUserToGroupCommand c, IGroupDirectory groups,
        IUserDirectory users, CancellationToken ct)
    {
        RequireTenant(c.TenantId);
        var group = await RequireGroup(groups, c.TenantId, c.GroupId, ct);
        var user = await users.FindByIdAsync(c.UserId, ct);
        if (user is null || user.TenantId != c.TenantId)
            throw DomainException.NotFound("user not found");
        await groups.AddMemberAsync(c.GroupId, c.UserId, ct);
        return new GroupDto(group.Id, group.Name, group.Description,
            group.Roles.Select(r => r.ToString()).ToList(),
            await groups.MemberCountAsync(group.Id, ct));
    }

    public static async Task<GroupDto> Handle(
        RemoveUserFromGroupCommand c, IGroupDirectory groups, CancellationToken ct)
    {
        RequireTenant(c.TenantId);
        var group = await RequireGroup(groups, c.TenantId, c.GroupId, ct);
        await groups.RemoveMemberAsync(c.GroupId, c.UserId, ct);
        return new GroupDto(group.Id, group.Name, group.Description,
            group.Roles.Select(r => r.ToString()).ToList(),
            await groups.MemberCountAsync(group.Id, ct));
    }

    public static async Task<OfficerUserResult> Handle(
        CreateOfficerUserCommand c, IUserDirectory users,
        IPasswordHasher hasher, CancellationToken ct)
    {
        RequireTenant(c.TenantId);
        if (string.IsNullOrWhiteSpace(c.Email))
            throw DomainException.BadRequest("email is required");
        var user = new LolaUser(
            $"u-{Guid.NewGuid():N}", c.TenantId, c.Email, c.DisplayName,
            string.IsNullOrEmpty(c.Password) ? null : hasher.Hash(c.Password),
            string.IsNullOrEmpty(c.Password) ? UserStatus.Invited : UserStatus.Active,
            MfaEnabled: false, DateTimeOffset.UtcNow.ToString("O"));
        await users.InsertAsync(user, ct);
        return new OfficerUserResult(user.Id, user.Email,
            user.Status.ToString().ToLowerInvariant());
    }
}
