using Dapper;
using Microsoft.Data.Sqlite;

namespace Lola.ControlPlane;

/// Auth-version bump + grant revocation primitives (MPE-275). Every
/// authority-changing mutation runs these inside ITS OWN control-plane
/// transaction, so the version increment and the token revocation are
/// committed or rolled back with the mutation itself — a caller can
/// never produce a privilege change with live stale grants.
internal static class AuthorityBump
{
    /// Bumps one user's auth version and revokes their outstanding
    /// OIDC grants (reference access tokens, refresh tokens, codes)
    /// on the caller's open transaction.
    public static async Task BumpUserAsync(
        SqliteConnection conn, SqliteTransaction tx, string userId,
        CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE users SET auth_version = auth_version + 1 WHERE id = @userId
            """, new { userId }, transaction: tx, cancellationToken: ct));
        await RevokeSubjectTokensAsync(conn, tx, userId, ct);
    }

    /// Revokes all still-valid grant rows for a subject. Reference
    /// tokens are validated against this table per request, so the
    /// next bearer call after commit is denied.
    public static async Task RevokeSubjectTokensAsync(
        SqliteConnection conn, SqliteTransaction tx, string subject,
        CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE openiddict_tokens SET status = 'revoked'
            WHERE subject = @subject AND status = 'valid'
            """, new { subject }, transaction: tx, cancellationToken: ct));
    }

    /// Bumps + revokes every current member of a group — used by
    /// group-role edits and deletion, which change authority for the
    /// membership as a whole. Runs BEFORE the mutation mutates
    /// user_groups so the member set is still readable.
    public static async Task BumpGroupMembersAsync(
        SqliteConnection conn, SqliteTransaction tx, string groupId,
        CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE users SET auth_version = auth_version + 1
            WHERE id IN (SELECT user_id FROM user_groups WHERE group_id = @groupId)
            """, new { groupId }, transaction: tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE openiddict_tokens SET status = 'revoked'
            WHERE status = 'valid' AND subject IN
                (SELECT user_id FROM user_groups WHERE group_id = @groupId)
            """, new { groupId }, transaction: tx, cancellationToken: ct));
    }
}
