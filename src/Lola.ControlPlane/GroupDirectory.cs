using Dapper;
using Lola.Application.Errors;
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
        // LEFT JOIN over real columns only — a computed JSON aggregate
        // column reports byte[] to Dapper on the zero-row path and
        // breaks materialization (found via AcceptInvite on a
        // group-less tenant, MPE-90).
        var rows = await conn.QueryAsync<GroupRoleRow>(new CommandDefinition("""
            SELECT g.id, g.tenant_id AS TenantId, g.name, g.description,
                   g.system_kind AS SystemKind, gr.role AS Role
            FROM groups g LEFT JOIN group_roles gr ON gr.group_id = g.id
            WHERE g.tenant_id = @tenantId ORDER BY g.name
            """, new { tenantId }, cancellationToken: ct));
        return ToGroups(rows);
    }

    public async Task<LolaGroup?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<GroupRoleRow>(new CommandDefinition("""
            SELECT g.id, g.tenant_id AS TenantId, g.name, g.description,
                   g.system_kind AS SystemKind, gr.role AS Role
            FROM groups g LEFT JOIN group_roles gr ON gr.group_id = g.id
            WHERE g.id = @id
            """, new { id }, cancellationToken: ct));
        return ToGroups(rows).FirstOrDefault();
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
        await RequireMutableAsync(conn, id, ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE groups SET
                name = COALESCE(@name, name),
                description = COALESCE(@description, description)
            WHERE id = @id
            """, new { id, name, description }, cancellationToken: ct));
    }

    /// Role edits change authority for every member — bump their auth
    /// versions and revoke their grants inside the same transaction
    /// (MPE-275). The bump runs first while membership is still legible.
    public async Task SetRolesAsync(
        string groupId, IReadOnlyCollection<LolaRole> roles, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await RequireMutableAsync(conn, groupId, ct);
        using var tx = conn.BeginTransaction();
        await AuthorityBump.BumpGroupMembersAsync(conn, tx, groupId, ct);
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

    /// Deletion removes members' authority — bump + revoke all current
    /// members in the same transaction, before the cascade deletes the
    /// membership rows (MPE-275).
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await RequireMutableAsync(conn, id, ct);
        using var tx = conn.BeginTransaction();
        await AuthorityBump.BumpGroupMembersAsync(conn, tx, id, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM groups WHERE id = @id", new { id },
            transaction: tx, cancellationToken: ct));
        tx.Commit();
    }

    /// Shared mutation boundary (MPE-273): a group carrying a
    /// system_kind is a protected invite target — never mutable
    /// through the admin directory no matter which caller got here.
    private static async Task RequireMutableAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn, string groupId,
        CancellationToken ct)
    {
        var kind = await conn.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT system_kind FROM groups WHERE id = @groupId",
                new { groupId }, cancellationToken: ct));
        if (kind is not null)
            throw DomainException.Forbidden(
                "system groups are protected from structural edits");
    }

    /// Membership changes alter effective roles — bump + revoke the
    /// user in the same transaction as the row write (MPE-275).
    /// No-ops (already a member / not a member) don't bump: no
    /// authority moved, so no session should die.
    public async Task AddMemberAsync(
        string groupId, string userId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var changed = await conn.ExecuteAsync(new CommandDefinition("""
            INSERT OR IGNORE INTO user_groups (user_id, group_id)
            VALUES (@userId, @groupId)
            """, new { userId, groupId }, transaction: tx, cancellationToken: ct));
        if (changed > 0)
            await AuthorityBump.BumpUserAsync(conn, tx, userId, ct);
        tx.Commit();
    }

    public async Task RemoveMemberAsync(
        string groupId, string userId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var changed = await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM user_groups WHERE user_id = @userId AND group_id = @groupId
            """, new { userId, groupId }, transaction: tx, cancellationToken: ct));
        if (changed > 0)
            await AuthorityBump.BumpUserAsync(conn, tx, userId, ct);
        tx.Commit();
    }

    public async Task<IReadOnlyList<string>> MemberIdsAsync(
        string groupId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var ids = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT user_id FROM user_groups WHERE group_id = @groupId",
            new { groupId }, cancellationToken: ct));
        return ids.AsList();
    }

    private sealed record GroupRoleRow(
        string Id, string TenantId, string Name, string? Description,
        string? SystemKind, string? Role);

    private static IReadOnlyList<LolaGroup> ToGroups(IEnumerable<GroupRoleRow> rows)
        => rows.GroupBy(r => r.Id).Select(g =>
            {
                var first = g.First();
                var roles = g.Where(r => r.Role is not null)
                    .Select(r => Enum.Parse<LolaRole>(r.Role!)).ToList();
                return new LolaGroup(first.Id, first.TenantId, first.Name,
                    first.Description, roles, first.SystemKind);
            }).ToList();
}
