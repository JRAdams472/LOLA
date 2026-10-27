using Lola.Application.Identity;

namespace Lola.Application.Admin.Groups;

public sealed record RemoveUserFromGroupCommand(string TenantId, string GroupId, string UserId);

public static class RemoveUserFromGroupHandler
{
    public static async Task<GroupDto> Handle(
        RemoveUserFromGroupCommand c, IGroupDirectory groups, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var group = await AdminGuards.RequireGroup(groups, c.TenantId, c.GroupId, ct);
        await groups.RemoveMemberAsync(c.GroupId, c.UserId, ct);
        return GroupDto.From(group, await groups.MemberCountAsync(group.Id, ct));
    }
}
