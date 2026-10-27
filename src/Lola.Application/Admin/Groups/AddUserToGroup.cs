using Lola.Application.Errors;
using Lola.Application.Identity;

namespace Lola.Application.Admin.Groups;

public sealed record AddUserToGroupCommand(string TenantId, string GroupId, string UserId);

public static class AddUserToGroupHandler
{
    public static async Task<GroupDto> Handle(
        AddUserToGroupCommand c, IGroupDirectory groups,
        IUserDirectory users, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var group = await AdminGuards.RequireGroup(groups, c.TenantId, c.GroupId, ct);
        var user = await users.FindByIdAsync(c.UserId, ct);
        if (user is null || user.TenantId != c.TenantId)
            throw DomainException.NotFound("user not found");
        await groups.AddMemberAsync(c.GroupId, c.UserId, ct);
        return GroupDto.From(group, await groups.MemberCountAsync(group.Id, ct));
    }
}
