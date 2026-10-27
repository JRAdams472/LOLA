using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.Application.Admin.Groups;

public sealed record CreateGroupCommand(
    string TenantId, string Name, string? Description, IReadOnlyList<LolaRole> Roles);

public static class CreateGroupHandler
{
    public static async Task<GroupDto> Handle(
        CreateGroupCommand c, IGroupDirectory groups, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var group = new LolaGroup($"g-{Guid.NewGuid():N}", c.TenantId,
            c.Name, c.Description, c.Roles);
        await groups.InsertAsync(group, ct);
        return GroupDto.From(group, 0);
    }
}
