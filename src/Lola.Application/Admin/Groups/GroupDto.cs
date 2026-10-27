using Lola.Domain.Identity;

namespace Lola.Application.Admin.Groups;

/// Admin group view — roles resolve through group_roles; memberCount
/// is the live user_groups count (MPE-86).
public sealed record GroupDto(
    string Id, string Name, string? Description,
    IReadOnlyList<string> Roles, int MemberCount)
{
    public static GroupDto From(LolaGroup g, int memberCount)
        => new(g.Id, g.Name, g.Description,
            g.Roles.Select(r => r.ToString()).ToList(), memberCount);
}
