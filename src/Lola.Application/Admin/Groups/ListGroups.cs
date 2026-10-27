using Lola.Application.Identity;

namespace Lola.Application.Admin.Groups;

public sealed record ListGroupsQuery(string TenantId);

public static class ListGroupsHandler
{
    public static async Task<IReadOnlyList<GroupDto>> Handle(
        ListGroupsQuery q, IGroupDirectory groups, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var result = new List<GroupDto>();
        foreach (var g in await groups.ListByTenantAsync(q.TenantId, ct))
            result.Add(GroupDto.From(g, await groups.MemberCountAsync(g.Id, ct)));
        return result;
    }
}
