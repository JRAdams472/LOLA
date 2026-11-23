using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Classification;
using Lola.Domain.Identity;

namespace Lola.ControlPlane;

/// Invite ledger over the control-plane DB (MPE-87). Stores only the
/// SHA-256 token hash — a DB read can never yield a usable token.
/// `email`/`org_name` are ciphertext under the owning tenant's transit
/// key with a tenant-scoped `email_idx` blind index (MPE-286).
public sealed class InviteDirectory(
    ControlPlaneDb db, IFieldCipher cipher, IBlindIndex index) : IInviteDirectory
{
    private const string Columns =
        "id, tenant_id AS TenantId, email, role, token_hash AS TokenHash, " +
        "expires_utc AS ExpiresUtc, used_utc AS UsedUtc, created_by AS CreatedBy, " +
        "org_name AS OrgName";

    public async Task InsertAsync(InviteRecord invite, CancellationToken ct = default)
    {
        var emailCt = await CpCrypto.EncryptAsync(cipher, invite.TenantId, invite.Email, ct);
        var orgCt = await CpCrypto.EncryptAsync(cipher, invite.TenantId, invite.OrgName, ct);
        var emailIdx = await CpCrypto.TenantIndexAsync(
            index, invite.TenantId, PiiClass.Contact, invite.Email, ct);
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO invites (id, tenant_id, email, email_idx, role, token_hash,
                                 expires_utc, created_by, org_name)
            VALUES (@Id, @TenantId, @Email, @EmailIdx, @Role, @TokenHash,
                    @ExpiresUtc, @CreatedBy, @OrgName)
            """, new
        {
            invite.Id,
            invite.TenantId,
            Email = emailCt,
            EmailIdx = emailIdx,
            Role = invite.Role.ToString(),
            invite.TokenHash,
            invite.ExpiresUtc,
            invite.CreatedBy,
            OrgName = orgCt
        }, cancellationToken: ct));
    }

    public async Task<InviteRecord?> FindByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<InviteRow>(
            new CommandDefinition(
                $"SELECT {Columns} FROM invites WHERE token_hash = @tokenHash",
                new { tokenHash }, cancellationToken: ct));
        if (row is null) return null;
        var email = await CpCrypto.DecryptTenantAsync(cipher, row.TenantId, row.Email, ct);
        var org = await CpCrypto.DecryptTenantAsync(cipher, row.TenantId, row.OrgName, ct);
        return new InviteRecord(row.Id, row.TenantId, email!,
            Enum.Parse<LolaRole>(row.Role, ignoreCase: true), row.TokenHash,
            row.ExpiresUtc, row.UsedUtc, row.CreatedBy, org);
    }

    public async Task<bool> MarkUsedAsync(string inviteId, string usedUtc, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var affected = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE invites SET used_utc = @usedUtc
            WHERE id = @inviteId AND used_utc IS NULL
            """, new { inviteId, usedUtc }, cancellationToken: ct));
        return affected == 1;
    }

    private sealed record InviteRow(
        string Id, string TenantId, string Email, string Role,
        string TokenHash, string ExpiresUtc, string? UsedUtc, string CreatedBy,
        string? OrgName);
}
