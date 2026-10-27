using Dapper;
using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.ControlPlane;

/// User directory over the control-plane DB — auth identifiers only.
public sealed class UserDirectory(ControlPlaneDb db) : IUserDirectory
{
    private const string Columns =
        "id, tenant_id AS TenantId, email, display_name AS DisplayName, " +
        "password_hash AS PasswordHash, status, " +
        "mfa_enabled AS MfaEnabled, created_utc AS CreatedUtc";

    public async Task<LolaUser?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            new CommandDefinition($"SELECT {Columns} FROM users WHERE id = @id",
                new { id }, cancellationToken: ct));
        return row?.ToUser();
    }

    public async Task<LolaUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = (await conn.QueryAsync<UserRow>(
            new CommandDefinition(
                $"SELECT {Columns} FROM users WHERE lower(email) = lower(@email) " +
                "AND status != 'disabled'",
                new { email }, cancellationToken: ct))).ToList();
        return rows.Count == 1 ? rows[0].ToUser() : null;
    }

    public async Task<IReadOnlyList<LolaUser>> ListByTenantAsync(string tenantId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<UserRow>(
            new CommandDefinition(
                $"SELECT {Columns} FROM users WHERE tenant_id = @tenantId ORDER BY email",
                new { tenantId }, cancellationToken: ct));
        return rows.Select(r => r.ToUser()).ToList();
    }

    public async Task<IReadOnlyList<LolaRole>> RolesForUserAsync(string userId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var roles = await conn.QueryAsync<string>(new CommandDefinition("""
            SELECT DISTINCT gr.role FROM group_roles gr
            JOIN user_groups ug ON ug.group_id = gr.group_id
            WHERE ug.user_id = @userId
            """, new { userId }, cancellationToken: ct));
        return roles.Select(Enum.Parse<LolaRole>).ToList();
    }

    public async Task InsertAsync(LolaUser user, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO users (id, tenant_id, email, display_name, password_hash, status, mfa_enabled)
            VALUES (@Id, @TenantId, @Email, @DisplayName, @PasswordHash, @Status, @MfaEnabled)
            """, new
        {
            user.Id,
            user.TenantId,
            user.Email,
            user.DisplayName,
            user.PasswordHash,
            Status = user.Status.ToString().ToLowerInvariant(),
            MfaEnabled = user.MfaEnabled ? 1 : 0
        }, cancellationToken: ct));
    }

    public async Task UpdatePasswordAsync(string userId, string passwordHash, string status, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE users SET password_hash = @passwordHash, status = @status,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @userId
            """, new { userId, passwordHash, status }, cancellationToken: ct));
    }

    public async Task SetStatusAsync(string userId, string status, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE users SET status = @status,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @userId
            """, new { userId, status }, cancellationToken: ct));
    }

    private sealed record UserRow(
        string Id, string TenantId, string Email, string DisplayName,
        string? PasswordHash, string Status, long MfaEnabled, string CreatedUtc)
    {
        public LolaUser ToUser() => new(Id, TenantId, Email, DisplayName, PasswordHash,
            Enum.Parse<UserStatus>(Status, ignoreCase: true), MfaEnabled != 0, CreatedUtc);
    }
}
