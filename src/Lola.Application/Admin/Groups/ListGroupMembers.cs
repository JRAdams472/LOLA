using Lola.Application.Identity;

namespace Lola.Application.Admin.Groups;

/// Member ids of one group (MPE-211) — the admin surface joins them
/// against the tenant user list it already holds. Cross-tenant group
/// ids 404 via RequireGroup so existence never leaks.
public sealed record ListGroupMembersQuery(string TenantId, string GroupId);

public static class ListGroupMembersHandler
{
    public static async Task<IReadOnlyList<string>> Handle(
        ListGroupMembersQuery q, IGroupDirectory groups, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var group = await AdminGuards.RequireGroup(groups, q.TenantId, q.GroupId, ct);
        return await groups.MemberIdsAsync(group.Id, ct);
    }
}
