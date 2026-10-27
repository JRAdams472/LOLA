using Lola.Application.Identity;

namespace Lola.Application.Admin.Groups;

public sealed record DeleteGroupCommand(string TenantId, string GroupId);

public static class DeleteGroupHandler
{
    public static async Task<GroupDto> Handle(
        DeleteGroupCommand c, IGroupDirectory groups, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var group = await AdminGuards.RequireGroup(groups, c.TenantId, c.GroupId, ct);
        await groups.DeleteAsync(group.Id, ct);
        return GroupDto.From(group, 0);
    }
}
