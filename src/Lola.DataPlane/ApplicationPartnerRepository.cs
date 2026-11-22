using Dapper;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Events;
using Microsoft.Data.Sqlite;

namespace Lola.DataPlane;

/// `application_partners` write/admin side (M19) — the partner-scope
/// reads a row guard needs live on IApplicationRepository; this repo
/// owns link, revoke, and the per-app link listing. Rows are
/// current-state: revoke stamps `revoked_utc` (soft delete for
/// provenance), re-link clears it — history rides loan_events/audit.
/// MPE-274: access changes commit state + audit + loan event inside
/// ONE BEGIN IMMEDIATE — an evidence gap after a granted scope was
/// the finding; a failed evidence write now takes the grant with it.
public sealed class ApplicationPartnerRepository(
    TenantConnectionFactory factory)
    : TenantRepository(factory), IApplicationPartnerRepository
{
    private readonly TenantConnectionFactory _factory = factory;

    /// Failure-injection seam (tests, InternalsVisibleTo): invoked
    /// with "state", "audit", or "event" immediately before each
    /// append inside the composite tx — throwing there proves the
    /// all-or-nothing contract.
    internal Action<string>? WriteProbe { get; set; }

    public async Task<PartnerLinkOutcome> CommitLinkAsync(
        PartnerLink link, AuditDraft audit, LoanEventDraft loanEvent,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            var active = await conn.QuerySingleOrDefaultAsync<string>(
                """
                SELECT partner_role FROM application_partners
                WHERE app_id = @AppId AND partner_user_id = @PartnerUserId
                  AND revoked_utc IS NULL
                """, new { link.AppId, link.PartnerUserId });
            if (active == link.PartnerRole)
            {
                await conn.ExecuteAsync("ROLLBACK");
                return PartnerLinkOutcome.NoOp;
            }
            WriteProbe?.Invoke("state");
            await conn.ExecuteAsync(
                """
                INSERT INTO application_partners
                    (app_id, partner_user_id, partner_role, linked_by,
                     linked_utc, revoked_utc)
                VALUES (@AppId, @PartnerUserId, @PartnerRole, @LinkedBy,
                        @LinkedUtc, NULL)
                ON CONFLICT (app_id, partner_user_id) DO UPDATE SET
                    partner_role = excluded.partner_role,
                    linked_by    = excluded.linked_by,
                    linked_utc   = excluded.linked_utc,
                    revoked_utc  = NULL
                """, new
                {
                    link.AppId,
                    link.PartnerUserId,
                    link.PartnerRole,
                    link.LinkedBy,
                    link.LinkedUtc
                });
            WriteProbe?.Invoke("audit");
            await new TenantAuditLog(_factory)
                .WriteOnConnectionAsync(audit, conn, ct);
            WriteProbe?.Invoke("event");
            await new LoanEventLog(_factory)
                .WriteOnConnectionAsync(loanEvent, conn, ct);
            await conn.ExecuteAsync("COMMIT");
            return PartnerLinkOutcome.Linked;
        }
        catch
        {
            await conn.ExecuteAsync("ROLLBACK");
            throw;
        }
    }

    public async Task<PartnerLinkOutcome> CommitRevokeAsync(
        string appId, string partnerUserId, string revokedUtc,
        AuditDraft audit, LoanEventDraft loanEvent,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            var active = await conn.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM application_partners
                WHERE app_id = @appId AND partner_user_id = @partnerUserId
                  AND revoked_utc IS NULL
                """, new { appId, partnerUserId });
            if (active == 0)
            {
                await conn.ExecuteAsync("ROLLBACK");
                return PartnerLinkOutcome.NotFound;
            }
            WriteProbe?.Invoke("state");
            await conn.ExecuteAsync(
                """
                UPDATE application_partners SET revoked_utc = @revokedUtc
                WHERE app_id = @appId AND partner_user_id = @partnerUserId
                  AND revoked_utc IS NULL
                """, new { appId, partnerUserId, revokedUtc });
            WriteProbe?.Invoke("audit");
            await new TenantAuditLog(_factory)
                .WriteOnConnectionAsync(audit, conn, ct);
            WriteProbe?.Invoke("event");
            await new LoanEventLog(_factory)
                .WriteOnConnectionAsync(loanEvent, conn, ct);
            await conn.ExecuteAsync("COMMIT");
            return PartnerLinkOutcome.Revoked;
        }
        catch
        {
            await conn.ExecuteAsync("ROLLBACK");
            throw;
        }
    }

    public async Task LinkAsync(PartnerLink link, CancellationToken ct = default)
        => await ExecuteAsync(
            """
            INSERT INTO application_partners
                (app_id, partner_user_id, partner_role, linked_by,
                 linked_utc, revoked_utc)
            VALUES (@AppId, @PartnerUserId, @PartnerRole, @LinkedBy,
                    @LinkedUtc, NULL)
            ON CONFLICT (app_id, partner_user_id) DO UPDATE SET
                partner_role = excluded.partner_role,
                linked_by    = excluded.linked_by,
                linked_utc   = excluded.linked_utc,
                revoked_utc  = NULL
            """, new
            {
                link.AppId,
                link.PartnerUserId,
                link.PartnerRole,
                link.LinkedBy,
                link.LinkedUtc
            }, ct);

    public async Task<bool> RevokeAsync(
        string appId, string partnerUserId, string revokedUtc,
        CancellationToken ct = default)
        => await ExecuteAsync(
            """
            UPDATE application_partners SET revoked_utc = @revokedUtc
            WHERE app_id = @appId AND partner_user_id = @partnerUserId
              AND revoked_utc IS NULL
            """, new { appId, partnerUserId, revokedUtc }, ct) == 1;

    public async Task<IReadOnlyList<PartnerLink>> ListForAppAsync(
        string appId, CancellationToken ct = default)
        => (await QueryAsync<LinkRow>(
            """
            SELECT app_id AS AppId, partner_user_id AS PartnerUserId,
                   partner_role AS PartnerRole, linked_by AS LinkedBy,
                   linked_utc AS LinkedUtc, revoked_utc AS RevokedUtc
            FROM application_partners
            WHERE app_id = @appId ORDER BY linked_utc DESC
            """, new { appId }, ct))
            .Select(r => new PartnerLink(r.AppId, r.PartnerUserId,
                r.PartnerRole, r.LinkedBy, r.LinkedUtc, r.RevokedUtc))
            .ToList();

    private sealed record LinkRow(
        string AppId, string PartnerUserId, string PartnerRole,
        string LinkedBy, string LinkedUtc, string? RevokedUtc);
}
