using Dapper;
using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.ControlPlane;

/// Group directory over the control-plane DB — group→role→visibility
/// is the only role assignment path (ADR-0005).
public sealed class GroupDirectory(ControlPlaneDb db) : IGroupDirectory
{
    public async Task<IReadOnlyList<LolaGroup>> ListByTenantAsync(
        string tenantId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<GroupRow>(new CommandDefinition("""
            SELECT g.id, g.tenant_id AS TenantId, g.name, g.description,
                   (SELECT json_group_array(role) FROM group_roles
                     WHERE group_id = g.id) AS RolesJson
            FROM groups g WHERE g.tenant_id = @tenantId ORDER BY g.name
            """, new { tenantId }, cancellationToken: ct));
        return rows.Select(r => r.ToGroup()).ToList();
    }

    public async Task<LolaGroup?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<GroupRow>(new CommandDefinition("""
            SELECT g.id, g.tenant_id AS TenantId, g.name, g.description,
                   (SELECT json_group_array(role) FROM group_roles
                     WHERE group_id = g.id) AS RolesJson
            FROM groups g WHERE g.id = @id
            """, new { id }, cancellationToken: ct));
        return row?.ToGroup();
    }

    public async Task<int> MemberCountAsync(string groupId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM user_groups WHERE group_id = @groupId",
            new { groupId }, cancellationToken: ct));
    }

    public async Task InsertAsync(LolaGroup group, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO groups (id, tenant_id, name, description)
            VALUES (@Id, @TenantId, @Name, @Description)
            """, new { group.Id, group.TenantId, group.Name, group.Description },
            transaction: tx, cancellationToken: ct));
        foreach (var role in group.Roles)
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO group_roles (group_id, role) VALUES (@gid, @role)
                """, new { gid = group.Id, role = role.ToString() },
                transaction: tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task UpdateAsync(
        string id, string? name, string? description, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE groups SET
                name = COALESCE(@name, name),
                description = COALESCE(@description, description)
            WHERE id = @id
            """, new { id, name, description }, cancellationToken: ct));
    }

    public async Task SetRolesAsync(
        string groupId, IReadOnlyCollection<LolaRole> roles, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM group_roles WHERE group_id = @groupId",
            new { groupId }, transaction: tx, cancellationToken: ct));
        foreach (var role in roles)
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO group_roles (group_id, role) VALUES (@groupId, @role)
                """, new { groupId, role = role.ToString() },
                transaction: tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM groups WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task AddMemberAsync(
        string groupId, string userId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT OR IGNORE INTO user_groups (user_id, group_id)
            VALUES (@userId, @groupId)
            """, new { userId, groupId }, cancellationToken: ct));
    }

    public async Task RemoveMemberAsync(
        string groupId, string userId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM user_groups WHERE user_id = @userId AND group_id = @groupId
            """, new { userId, groupId }, cancellationToken: ct));
    }

    private sealed record GroupRow(
        string Id, string TenantId, string Name, string? Description, string RolesJson)
    {
        public LolaGroup ToGroup()
        {
            var roles = string.IsNullOrEmpty(RolesJson)
                ? new List<LolaRole>()
                : System.Text.Json.JsonSerializer
                    .Deserialize<List<string>>(RolesJson)!
                    .Select(Enum.Parse<LolaRole>).ToList();
            return new LolaGroup(Id, TenantId, Name, Description, roles);
        }
    }
}
