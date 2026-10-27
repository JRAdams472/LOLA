using Dapper;
using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.ControlPlane;

/// Invite ledger over the control-plane DB (MPE-87). Stores only the
/// SHA-256 token hash — a DB read can never yield a usable token.
public sealed class InviteDirectory(ControlPlaneDb db) : IInviteDirectory
{
    private const string Columns =
        "id, tenant_id AS TenantId, email, role, token_hash AS TokenHash, " +
        "expires_utc AS ExpiresUtc, used_utc AS UsedUtc, created_by AS CreatedBy";

    public async Task InsertAsync(InviteRecord invite, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO invites (id, tenant_id, email, role, token_hash,
                                 expires_utc, created_by)
            VALUES (@Id, @TenantId, @Email, @Role, @TokenHash,
                    @ExpiresUtc, @CreatedBy)
            """, new
        {
            invite.Id,
            invite.TenantId,
            invite.Email,
            Role = invite.Role.ToString(),
            invite.TokenHash,
            invite.ExpiresUtc,
            invite.CreatedBy
        }, cancellationToken: ct));
    }

    public async Task<InviteRecord?> FindByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<InviteRow>(
            new CommandDefinition(
                $"SELECT {Columns} FROM invites WHERE token_hash = @tokenHash",
                new { tokenHash }, cancellationToken: ct));
        return row?.ToInvite();
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
        string TokenHash, string ExpiresUtc, string? UsedUtc, string CreatedBy)
    {
        public InviteRecord ToInvite() => new(Id, TenantId, Email,
            Enum.Parse<LolaRole>(Role, ignoreCase: true), TokenHash, ExpiresUtc,
            UsedUtc, CreatedBy);
    }
}
