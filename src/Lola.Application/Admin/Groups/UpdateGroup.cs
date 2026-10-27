using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.Application.Admin.Groups;

public sealed record UpdateGroupCommand(
    string TenantId, string GroupId, string? Name, string? Description,
    IReadOnlyList<LolaRole>? Roles);

public static class UpdateGroupHandler
{
    public static async Task<GroupDto> Handle(
        UpdateGroupCommand c, IGroupDirectory groups, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        await AdminGuards.RequireGroup(groups, c.TenantId, c.GroupId, ct);
        if (c.Name is not null || c.Description is not null)
            await groups.UpdateAsync(c.GroupId, c.Name, c.Description, ct);
        if (c.Roles is not null)
            await groups.SetRolesAsync(c.GroupId, c.Roles, ct);
        var updated = (await groups.FindByIdAsync(c.GroupId, ct))!;
        return GroupDto.From(updated, await groups.MemberCountAsync(updated.Id, ct));
    }
}
