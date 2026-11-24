using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Lola.Application.Security;

namespace Lola.ControlPlane;

/// Decrypted view of a BFF session row — exists only inside the API;
/// never serializes to a response (endpoints project the AT field
/// alone). `rt` is the IdP refresh grant the API holds in custody.
public sealed record BffSession(
    string Namespace, string Subject, string TenantId, long AuthVersion,
    string AccessToken, string? RefreshToken, string? Name,
    long AtExpMs, long? RtExpMs, long LastActiveMs, long AbsExpMs,
    long? IdleMs, long Rev, long? RevokedAtMs);

/// Server-side BFF session store (MPE-289 / remediation phase 9): the
/// browser carries an opaque handle; token material stays API-side as
/// `control-plane`-domain transit ciphertext. Lookups are SHA-256
/// hashes scoped to the caller's namespace, so a console handle can
/// never resolve through the portal's credentials. Refresh rotation is
/// single-flight per handle — `GateFor` serializes contenders and the
/// `rev` CAS makes a losing writer impossible.
public sealed class BffSessionStore(ControlPlaneDb db, IFieldCipher cipher)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    /// SHA-256 of the opaque browser handle — the only lookup key that
    /// ever reaches the database.
    public static string HashHandle(string handle) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(handle)))
            .ToLowerInvariant();

    /// Refresh single-flight: contenders for the same handle queue here;
    /// the loser re-reads the rotated row instead of minting a second
    /// grant (which reuse detection would kill the session over).
    public static SemaphoreSlim GateFor(string handleHash) =>
        Gates.GetOrAdd(handleHash, _ => new SemaphoreSlim(1, 1));

    public async Task CreateAsync(
        string ns, string handleHash, string subject, string tenantId,
        long authVersion, string at, string? rt, string? name,
        long atExpMs, long? rtExpMs, long absExpMs, long? idleMs,
        long nowMs, CancellationToken ct)
    {
        var atCt = await CpCrypto.EncryptGlobalAsync(cipher, at, ct);
        var rtCt = await CpCrypto.EncryptGlobalAsync(cipher, rt, ct);
        var nameCt = await CpCrypto.EncryptGlobalAsync(cipher, name, ct);
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO bff_sessions
                (namespace, handle_hash, subject, tenant_id, auth_version,
                 at_ct, rt_ct, name_ct, at_exp_ms, rt_exp_ms,
                 last_active_ms, abs_exp_ms, idle_ms, rev,
                 created_ms, revoked_at_ms)
            VALUES (@ns, @hash, @subject, @tenantId, @authVersion,
                    @atCt, @rtCt, @nameCt, @atExpMs, @rtExpMs,
                    @nowMs, @absExpMs, @idleMs, 0, @nowMs, NULL)
            """,
            new
            {
                ns,
                hash = handleHash,
                subject,
                tenantId,
                authVersion,
                atCt,
                rtCt,
                nameCt,
                atExpMs,
                rtExpMs,
                absExpMs,
                idleMs,
                nowMs,
            }, cancellationToken: ct));
    }

    /// Namespace-scoped read — an out-of-namespace handle is simply not
    /// found (wrong-client access fails as "no session", never as data).
    public async Task<BffSession?> ReadAsync(
        string ns, string handleHash, CancellationToken ct)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition("""
                SELECT subject, tenant_id AS TenantId, auth_version AS AuthVersion,
                       at_ct AS AtCt, rt_ct AS RtCt, name_ct AS NameCt,
                       at_exp_ms AS AtExpMs, rt_exp_ms AS RtExpMs,
                       last_active_ms AS LastActiveMs, abs_exp_ms AS AbsExpMs,
                       idle_ms AS IdleMs, rev AS Rev, revoked_at_ms AS RevokedAtMs
                FROM bff_sessions
                WHERE namespace = @ns AND handle_hash = @hash
                """,
                new { ns, hash = handleHash }, cancellationToken: ct));
        if (row is null) return null;
        return new BffSession(
            ns, row.Subject, row.TenantId, row.AuthVersion,
            (await CpCrypto.DecryptGlobalAsync(cipher, row.AtCt, ct))!,
            await CpCrypto.DecryptGlobalAsync(cipher, row.RtCt, ct),
            await CpCrypto.DecryptGlobalAsync(cipher, row.NameCt, ct),
            row.AtExpMs, row.RtExpMs, row.LastActiveMs, row.AbsExpMs,
            row.IdleMs, row.Rev, row.RevokedAtMs);
    }

    /// Slide the idle window only — absolute expiry is write-once at
    /// create and can never be extended.
    public async Task<bool> TouchAsync(
        string ns, string handleHash, long nowMs, CancellationToken ct)
    {
        using var conn = db.Open();
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE bff_sessions SET last_active_ms = @nowMs
            WHERE namespace = @ns AND handle_hash = @hash AND revoked_at_ms IS NULL
            """,
            new { ns, hash = handleHash, nowMs }, cancellationToken: ct)) > 0;
    }

    /// CAS rotation: succeeds only when the row still sits at
    /// `expectedRev` — a contender that already rotated wins the race
    /// and this write is a no-op the caller detects by re-reading.
    public async Task<bool> RotateAsync(
        string ns, string handleHash, long expectedRev,
        string at, string? rt, string? name,
        long atExpMs, long? rtExpMs, long lastActiveMs, CancellationToken ct)
    {
        var atCt = await CpCrypto.EncryptGlobalAsync(cipher, at, ct);
        var rtCt = await CpCrypto.EncryptGlobalAsync(cipher, rt, ct);
        var nameCt = await CpCrypto.EncryptGlobalAsync(cipher, name, ct);
        using var conn = db.Open();
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE bff_sessions SET
                at_ct = @atCt, rt_ct = @rtCt, name_ct = @nameCt,
                at_exp_ms = @atExpMs, rt_exp_ms = @rtExpMs,
                last_active_ms = @lastActiveMs, rev = rev + 1
            WHERE namespace = @ns AND handle_hash = @hash
              AND rev = @expectedRev AND revoked_at_ms IS NULL
            """,
            new
            {
                ns,
                hash = handleHash,
                expectedRev,
                atCt,
                rtCt,
                nameCt,
                atExpMs,
                rtExpMs,
                lastActiveMs,
            }, cancellationToken: ct)) > 0;
    }

    /// Revocation marks the live row dead and returns the held refresh
    /// token so the caller can kill the grant at the IdP — the row
    /// alone dying would leave a live rt. `found:false` means no live
    /// session under that handle (unknown or already revoked).
    public async Task<(bool Found, string? Rt)> RevokeAsync(
        string ns, string handleHash, long nowMs, CancellationToken ct)
    {
        using var conn = db.Open();
        var rtCt = await conn.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition("""
                SELECT rt_ct FROM bff_sessions
                WHERE namespace = @ns AND handle_hash = @hash AND revoked_at_ms IS NULL
                """,
                new { ns, hash = handleHash }, cancellationToken: ct));
        if (rtCt is null && await conn.ExecuteScalarAsync<int>(
                new CommandDefinition("""
                    SELECT COUNT(1) FROM bff_sessions
                    WHERE namespace = @ns AND handle_hash = @hash AND revoked_at_ms IS NULL
                    """,
                    new { ns, hash = handleHash }, cancellationToken: ct)) == 0)
            return (false, null);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE bff_sessions SET revoked_at_ms = @nowMs
            WHERE namespace = @ns AND handle_hash = @hash AND revoked_at_ms IS NULL
            """,
            new { ns, hash = handleHash, nowMs }, cancellationToken: ct));
        return (true, await CpCrypto.DecryptGlobalAsync(cipher, rtCt, ct));
    }

    /// Marker revoke when no rt is stored (or after grant theft): the
    /// session must die even with nothing to revoke upstream.
    public async Task MarkRevokedAsync(
        string ns, string handleHash, long nowMs, CancellationToken ct)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE bff_sessions SET revoked_at_ms = @nowMs
            WHERE namespace = @ns AND handle_hash = @hash AND revoked_at_ms IS NULL
            """,
            new { ns, hash = handleHash, nowMs }, cancellationToken: ct));
    }

    private sealed class Row
    {
        public string Subject { get; init; } = "";
        public string TenantId { get; init; } = "";
        public long AuthVersion { get; init; }
        public string AtCt { get; init; } = "";
        public string? RtCt { get; init; }
        public string? NameCt { get; init; }
        public long AtExpMs { get; init; }
        public long? RtExpMs { get; init; }
        public long LastActiveMs { get; init; }
        public long AbsExpMs { get; init; }
        public long? IdleMs { get; init; }
        public long Rev { get; init; }
        public long? RevokedAtMs { get; init; }
    }
}
