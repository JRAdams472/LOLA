using Lola.Application.Applications;

namespace Lola.DataPlane;

/// `application_partners` write/admin side (M19) — the partner-scope
/// reads a row guard needs live on IApplicationRepository; this repo
/// owns link, revoke, and the per-app link listing. Rows are
/// current-state: revoke stamps `revoked_utc` (soft delete for
/// provenance), re-link clears it — history rides loan_events/audit.
public sealed class ApplicationPartnerRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IApplicationPartnerRepository
{
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
