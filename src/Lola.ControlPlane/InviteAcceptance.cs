using Dapper;
using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Classification;
using Lola.Domain.Identity;

namespace Lola.ControlPlane;

/// Invite acceptance on ONE control-plane transaction (MPE-273).
/// BEGIN IMMEDIATE takes the SQLite write lock up front, so every
/// in-transaction recheck sees the latest committed state — a losing
/// concurrent accept blocks, then reads the winner's consumed invite
/// and fails clean, and two different first invites of the same kind
/// converge on one protected group. Any failed predicate, uniqueness
/// race or storage error rolls back all writes: no orphan account,
/// no half grant.
///
/// Field crypto (MPE-286): every encrypt/decrypt/index call runs in
/// <see cref="PrepareAsync"/> BEFORE the write transaction — transit
/// round-trips never hold the SQLite write lock. In-transaction SQL
/// only binds the pre-computed ciphertext and blind-index tokens.
public sealed class InviteAcceptance(
    ControlPlaneDb db, IFieldCipher cipher, IBlindIndex index) : IInviteAcceptance
{
    public async Task<InviteAcceptanceOutcome> AcceptAsync(
        InviteAcceptanceRequest r, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(r, ct);
        using var conn = db.Open();
        await conn.ExecuteAsync(
            new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: ct));
        try
        {
            var outcome = await RunAsync(conn, r, prepared, ct);
            await conn.ExecuteAsync(
                new CommandDefinition("COMMIT", cancellationToken: ct));
            return outcome;
        }
        catch
        {
            await conn.ExecuteAsync(new CommandDefinition("ROLLBACK",
                cancellationToken: CancellationToken.None));
            throw;
        }
    }

    /// Pre-transaction crypto: resolve the invite row, decrypt its
    /// identity fields under the tenant key, and compute every
    /// ciphertext/blind-index value the writes will bind. The in-tx
    /// recheck re-reads the row — a stale pre-read can only cause a
    /// valid failure, never a wrong write.
    private async Task<Prepared> PrepareAsync(
        InviteAcceptanceRequest r, CancellationToken ct)
    {
        using var conn = db.Open();
        var invite = await conn.QuerySingleOrDefaultAsync<InviteRow>(
            new CommandDefinition("""
                SELECT id, tenant_id AS TenantId, email, role,
                       expires_utc AS ExpiresUtc, used_utc AS UsedUtc,
                       org_name AS OrgName
                FROM invites WHERE token_hash = @TokenHash
                """, r, cancellationToken: ct))
            ?? throw DomainException.BadRequest("invite is invalid");

        var email = await CpCrypto.DecryptTenantAsync(
            cipher, invite.TenantId, invite.Email, ct);
        var org = await CpCrypto.DecryptTenantAsync(
            cipher, invite.TenantId, invite.OrgName, ct);
        var role = Enum.Parse<LolaRole>(invite.Role, ignoreCase: true);
        var kind = SystemGroups.ForRole(role)
            ?? throw DomainException.BadRequest(
                "invite role is not acceptable through this flow");
        return new Prepared(
            Invite: invite,
            EmailCipher: await CpCrypto.EncryptAsync(cipher, invite.TenantId, email, ct),
            EmailIdx: await CpCrypto.TenantIndexAsync(
                index, invite.TenantId, PiiClass.Contact, email!, ct),
            DisplayNameCipher: await CpCrypto.EncryptAsync(
                cipher, invite.TenantId, r.DisplayName, ct),
            OrgCipher: await CpCrypto.EncryptAsync(cipher, invite.TenantId, org, ct),
            GroupNameCipher: await CpCrypto.EncryptAsync(
                cipher, invite.TenantId, kind.Name, ct),
            GroupNameIdx: await CpCrypto.TenantIndexAsync(
                index, invite.TenantId, PiiClass.Other, kind.Name, ct),
            GroupDescCipher: await CpCrypto.EncryptAsync(
                cipher, invite.TenantId, kind.Description, ct),
            Kind: kind);
    }

    private static async Task<InviteAcceptanceOutcome> RunAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        InviteAcceptanceRequest r, Prepared p, CancellationToken ct)
    {
        var invite = await conn.QuerySingleOrDefaultAsync<InviteRow>(
            new CommandDefinition("""
                SELECT id, tenant_id AS TenantId, email, role,
                       expires_utc AS ExpiresUtc, used_utc AS UsedUtc,
                       org_name AS OrgName
                FROM invites WHERE token_hash = @TokenHash
                """, r, cancellationToken: ct))
            ?? throw DomainException.BadRequest("invite is invalid");
        if (invite.UsedUtc is not null)
            throw DomainException.BadRequest(
                "invite has already been used");
        if (DateTimeOffset.Parse(invite.ExpiresUtc) <
                DateTimeOffset.UtcNow)
            throw DomainException.BadRequest("invite has expired");
        var role = Enum.Parse<LolaRole>(invite.Role, ignoreCase: true);
        _ = SystemGroups.ForRole(role)
            ?? throw DomainException.BadRequest(
                "invite role is not acceptable through this flow");
        var dupe = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition("""
                SELECT count(*) FROM users
                WHERE tenant_id = @TenantId AND email_idx = @EmailIdx
                """, new { invite.TenantId, p.EmailIdx }, cancellationToken: ct));
        if (dupe > 0)
            throw DomainException.BadRequest(
                "a user with this email already exists");

        // Resolve the protected group by (tenant, system_kind) — never
        // by the editable name.
        var group = await conn.QuerySingleOrDefaultAsync<GroupRow>(
            new CommandDefinition("""
                SELECT id, name FROM groups
                WHERE tenant_id = @TenantId AND system_kind = @kind
                """, new { invite.TenantId, kind = p.Kind.SystemKind },
                cancellationToken: ct));
        string groupId;
        if (group is not null)
        {
            // A protected group whose role set drifted from the
            // expected singleton fails closed for operator review.
            var roles = (await conn.QueryAsync<string>(
                new CommandDefinition("""
                    SELECT role FROM group_roles WHERE group_id = @Id
                    """, group, cancellationToken: ct))).ToList();
            if (roles.Count != 1 || roles[0] != role.ToString())
                throw DomainException.Conflict(
                    $"protected group {group.Id} role set drifted — " +
                    "operator reconciliation required");
            groupId = group.Id;
        }
        else
        {
            // A non-system group squatting on the reserved alias would
            // shadow the grant — block, report the opaque id, never
            // silently promote its members.
            var squatter = await conn.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition("""
                    SELECT id FROM groups
                    WHERE tenant_id = @TenantId AND name_idx = @nameIdx
                    """, new { invite.TenantId, nameIdx = p.GroupNameIdx },
                    cancellationToken: ct));
            if (squatter is not null)
                throw DomainException.Conflict(
                    $"group {squatter} occupies a reserved system " +
                    "identity — operator reconciliation required");
            groupId = $"g-{Guid.NewGuid():N}";
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO groups (id, tenant_id, name, name_idx,
                                    description, system_kind)
                VALUES (@id, @TenantId, @name, @nameIdx, @desc, @kind)
                """, new
            {
                id = groupId,
                invite.TenantId,
                name = p.GroupNameCipher,
                nameIdx = p.GroupNameIdx,
                desc = p.GroupDescCipher,
                kind = p.Kind.SystemKind,
            }, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO group_roles (group_id, role)
                VALUES (@groupId, @role)
                """, new { groupId, role = role.ToString() },
                cancellationToken: ct));
        }

        var userId = $"u-{Guid.NewGuid():N}";
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO users (id, tenant_id, email, email_idx,
                               display_name, password_hash, status,
                               mfa_enabled, org_name)
            VALUES (@userId, @TenantId, @Email, @EmailIdx,
                    @DisplayName, @PasswordHash, 'active', 0, @OrgName)
            """, new
        {
            userId,
            invite.TenantId,
            Email = p.EmailCipher,
            EmailIdx = p.EmailIdx,
            DisplayName = p.DisplayNameCipher,
            r.PasswordHash,
            OrgName = p.OrgCipher,
        }, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO user_groups (user_id, group_id)
            VALUES (@userId, @groupId)
            """, new { userId, groupId }, cancellationToken: ct));

        // Single-use consume LAST: a lost race (used_utc set between
        // our checks and now) still rolls the whole acceptance back.
        var consumed = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE invites SET used_utc = @now
            WHERE id = @Id AND used_utc IS NULL
            """, new
        {
            invite.Id,
            now = DateTimeOffset.UtcNow.ToString("O"),
        }, cancellationToken: ct));
        if (consumed != 1)
            throw DomainException.BadRequest(
                "invite has already been used");

        return new InviteAcceptanceOutcome(userId, invite.TenantId);
    }

    private sealed record InviteRow(
        string Id, string TenantId, string Email, string Role,
        string ExpiresUtc, string? UsedUtc, string? OrgName);

    private sealed record GroupRow(string Id, string Name);

    private sealed record Prepared(
        InviteRow Invite,
        string? EmailCipher,
        string EmailIdx,
        string? DisplayNameCipher,
        string? OrgCipher,
        string? GroupNameCipher,
        string GroupNameIdx,
        string? GroupDescCipher,
        SystemGroups.Kind Kind);
}
