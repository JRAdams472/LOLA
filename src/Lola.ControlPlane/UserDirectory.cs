using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Classification;
using Lola.Domain.Identity;

namespace Lola.ControlPlane;

/// User directory over the control-plane DB — auth identifiers only.
/// `email`/`display_name`/`org_name` are randomized ciphertext under
/// the owning tenant's transit key (MPE-286); `email_idx` is the keyed
/// normalized blind index that makes login's equality lookup possible
/// without a plaintext column. Rows never carry readable identity.
public sealed class UserDirectory(
    ControlPlaneDb db, IFieldCipher cipher, IBlindIndex index) : IUserDirectory
{
    private const string Columns =
        "id, tenant_id AS TenantId, email, display_name AS DisplayName, " +
        "password_hash AS PasswordHash, status, " +
        "mfa_enabled AS MfaEnabled, created_utc AS CreatedUtc, " +
        "mfa_secret_ref AS MfaSecretRef, org_name AS OrgName, " +
        "auth_version AS AuthVersion";

    public async Task<LolaUser?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            new CommandDefinition($"SELECT {Columns} FROM users WHERE id = @id",
                new { id }, cancellationToken: ct));
        return row is null ? null : await ToUserAsync(row, ct);
    }

    /// Tenant-scoped equality lookup: the HMAC'd `email_idx` matches
    /// under the tenant's idx key — the plaintext email never reaches
    /// the query or the file.
    public async Task<LolaUser?> FindByEmailInTenantAsync(
        string tenantId, string email, CancellationToken ct = default)
    {
        var idx = await CpCrypto.TenantIndexAsync(
            index, tenantId, PiiClass.Contact, email, ct);
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            new CommandDefinition(
                $"SELECT {Columns} FROM users WHERE tenant_id = @tenantId " +
                "AND email_idx = @idx",
                new { tenantId, idx }, cancellationToken: ct));
        return row is null ? null : await ToUserAsync(row, ct);
    }

    /// Ciphertext can't sort — ordering happens after decrypt so the
    /// list stays email-sorted for the admin surface.
    public async Task<IReadOnlyList<LolaUser>> ListByTenantAsync(string tenantId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<UserRow>(
            new CommandDefinition(
                $"SELECT {Columns} FROM users WHERE tenant_id = @tenantId ORDER BY id",
                new { tenantId }, cancellationToken: ct));
        var users = new List<LolaUser>();
        foreach (var row in rows)
            users.Add(await ToUserAsync(row, ct));
        return users.OrderBy(u => u.Email, StringComparer.OrdinalIgnoreCase).ToList();
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
        var emailCt = await CpCrypto.EncryptAsync(cipher, user.TenantId, user.Email, ct);
        var nameCt = await CpCrypto.EncryptAsync(cipher, user.TenantId, user.DisplayName, ct);
        var orgCt = await CpCrypto.EncryptAsync(cipher, user.TenantId, user.OrgName, ct);
        var emailIdx = await CpCrypto.TenantIndexAsync(
            index, user.TenantId, PiiClass.Contact, user.Email, ct);
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO users (id, tenant_id, email, email_idx, display_name,
                               password_hash, status, mfa_enabled,
                               mfa_secret_ref, org_name)
            VALUES (@Id, @TenantId, @Email, @EmailIdx, @DisplayName,
                    @PasswordHash, @Status, @MfaEnabled, @MfaSecretRef, @OrgName)
            """, new
        {
            user.Id,
            user.TenantId,
            Email = emailCt,
            EmailIdx = emailIdx,
            DisplayName = nameCt,
            user.PasswordHash,
            Status = user.Status.ToString().ToLowerInvariant(),
            MfaEnabled = user.MfaEnabled ? 1 : 0,
            user.MfaSecretRef,
            OrgName = orgCt
        }, cancellationToken: ct));
    }

    /// Password/security reset (MPE-275): the credential change, the
    /// auth-version bump and the grant revocation commit together —
    /// stale sessions die with the transaction, never after it.
    public async Task UpdatePasswordAsync(string userId, string passwordHash, string status, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE users SET password_hash = @passwordHash, status = @status,
                auth_version = auth_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @userId
            """, new { userId, passwordHash, status }, transaction: tx, cancellationToken: ct));
        await AuthorityBump.RevokeSubjectTokensAsync(conn, tx, userId, ct);
        tx.Commit();
    }

    /// Any status transition (disable being the security-relevant one)
    /// bumps the auth version and revokes outstanding grants in the
    /// same transaction (MPE-275).
    public async Task SetStatusAsync(string userId, string status, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE users SET status = @status,
                auth_version = auth_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @userId
            """, new { userId, status }, transaction: tx, cancellationToken: ct));
        await AuthorityBump.RevokeSubjectTokensAsync(conn, tx, userId, ct);
        tx.Commit();
    }

    private async Task<LolaUser> ToUserAsync(UserRow row, CancellationToken ct)
    {
        var email = await CpCrypto.DecryptTenantAsync(cipher, row.TenantId, row.Email, ct);
        var name = await CpCrypto.DecryptTenantAsync(cipher, row.TenantId, row.DisplayName, ct);
        var org = await CpCrypto.DecryptTenantAsync(cipher, row.TenantId, row.OrgName, ct);
        return new LolaUser(row.Id, row.TenantId, email!, name!, row.PasswordHash,
            Enum.Parse<UserStatus>(row.Status, ignoreCase: true), row.MfaEnabled != 0,
            row.CreatedUtc, row.MfaSecretRef, org, row.AuthVersion);
    }

    private sealed record UserRow(
        string Id, string TenantId, string Email, string? DisplayName,
        string? PasswordHash, string Status, long MfaEnabled, string CreatedUtc,
        string? MfaSecretRef, string? OrgName, long AuthVersion);
}
