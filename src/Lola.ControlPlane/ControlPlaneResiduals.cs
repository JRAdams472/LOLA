using Dapper;
using Lola.Application.Identity;

namespace Lola.ControlPlane;

/// Post-erasure control-plane lifecycle (MPE-108/119). The crypto-shred
/// removes the tenant FILE; this removes what the file never contained:
/// the tenant's identity rows in the control-plane DB. One transaction
/// so a partial cleanup can't leave half-live credentials — a failure
/// rolls back and the eraser's retry runs the whole pass again.
///
/// Retained (the explicit audit spine): `tenants` (status `retired`),
/// `tenant_migrations`, `audit_events`. Everything else for the tenant
/// is deleted or anonymized:
///   - `openiddict_tokens`/`_authorizations` — subjects = the tenant's
///     user ids (interactive logins carry sub=user.Id) plus the
///     `svc-{tenant}` client_id and that client's application_id
///     (client-credentials tokens).
///   - `openiddict_applications` — the `svc-{tenant}` client row itself.
///   - `invites` — outstanding single-use invites.
///   - `groups`, `user_groups`, `group_roles` — role structure.
///   - `users` — anonymized, not deleted: user ids are FK-free history
///     references (audit actors, created_by), so the row survives as a
///     tombstone with no usable credential or PII-class field.
public sealed class ControlPlaneResiduals(ControlPlaneDb db) : IControlPlaneResiduals
{
    public Task<ResidualCleanup> CleanAsync(
        string tenantId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var svc = $"svc-{tenantId}";

        var tokens = conn.Execute(
            """
            DELETE FROM openiddict_tokens
            WHERE subject IN (SELECT id FROM users WHERE tenant_id = @tenantId)
               OR subject = @svc
               OR application_id IN (SELECT id FROM openiddict_applications
                                      WHERE client_id = @svc)
            """, new { tenantId, svc }, tx);

        var authorizations = conn.Execute(
            """
            DELETE FROM openiddict_authorizations
            WHERE subject IN (SELECT id FROM users WHERE tenant_id = @tenantId)
               OR subject = @svc
               OR application_id IN (SELECT id FROM openiddict_applications
                                      WHERE client_id = @svc)
            """, new { tenantId, svc }, tx);

        var clients = conn.Execute(
            "DELETE FROM openiddict_applications WHERE client_id = @svc",
            new { svc }, tx);

        var invites = conn.Execute(
            "DELETE FROM invites WHERE tenant_id = @tenantId",
            new { tenantId }, tx);

        conn.Execute(
            """
            DELETE FROM user_groups
            WHERE user_id IN (SELECT id FROM users WHERE tenant_id = @tenantId)
               OR group_id IN (SELECT id FROM groups WHERE tenant_id = @tenantId)
            """, new { tenantId }, tx);
        conn.Execute(
            """
            DELETE FROM group_roles
            WHERE group_id IN (SELECT id FROM groups WHERE tenant_id = @tenantId)
            """, new { tenantId }, tx);
        var groups = conn.Execute(
            "DELETE FROM groups WHERE tenant_id = @tenantId",
            new { tenantId }, tx);

        // Anonymize rather than delete: audit actors and invites.created_by
        // reference user ids — a tombstone keeps those joins resolvable
        // while carrying no credential and no PII-class field. The email
        // shape stays unique per (tenant_id, email) via the user id.
        var users = conn.Execute(
            """
            UPDATE users
            SET email          = 'erased-' || id || '@erased',
                display_name   = 'erased',
                password_hash  = NULL,
                mfa_enabled    = 0,
                mfa_secret_ref = NULL,
                status         = 'disabled',
                updated_utc    = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE tenant_id = @tenantId
            """, new { tenantId }, tx);

        tx.Commit();
        return Task.FromResult(new ResidualCleanup(
            tokens, authorizations, clients, invites, groups, users));
    }
}
