using Dapper;
using Lola.Application.Identity;
using Lola.Domain.Classification;
using Lola.Domain.Identity;

namespace Lola.ControlPlane;

/// role_visibility lookup — unions the 'plaintext' grants across the
/// caller's roles; absent rows mean masked (ADR-0008).
public sealed class RoleVisibilityMap(ControlPlaneDb db) : IRoleVisibilityMap
{
    public async Task<IReadOnlySet<PiiClass>> VisibleClassesAsync(
        IReadOnlyCollection<LolaRole> roles, CancellationToken ct = default)
    {
        if (roles.Count == 0) return new HashSet<PiiClass>();
        using var conn = db.Open();
        var names = roles.Select(r => r.ToString()).ToArray();
        var classes = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT pii_class FROM role_visibility
            WHERE access = 'plaintext' AND role IN @names
            """, new { names }, cancellationToken: ct));
        return classes.Select(Enum.Parse<PiiClass>).ToHashSet();
    }
}
