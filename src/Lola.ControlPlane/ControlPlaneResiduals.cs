using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Classification;

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
///
/// Tombstone ciphertext (MPE-286): residuals run AFTER the tenant's
/// transit keys are destroyed (keys-first shredding, MPE-96), so the
/// synthetic tombstone values encrypt under the `control-plane` key
/// domain — `DecryptTenantAsync` retries that domain when the tenant
/// key is gone. The svc-client match likewise keys on `client_id_idx`
/// because `client_id` itself is ciphertext.
public sealed class ControlPlaneResiduals(
    ControlPlaneDb db, IFieldCipher cipher, IBlindIndex index) : IControlPlaneResiduals
{
    public async Task<ResidualCleanup> CleanAsync(
        string tenantId, CancellationToken ct = default)
    {
        var svc = $"svc-{tenantId}";
        var svcIdx = await CpCrypto.GlobalIndexAsync(
            index, PiiClass.Other, svc, ct);
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        var tokens = conn.Execute(
            """
            DELETE FROM openiddict_tokens
            WHERE subject IN (SELECT id FROM users WHERE tenant_id = @tenantId)
               OR subject = @svc
               OR application_id IN (SELECT id FROM openiddict_applications
                                      WHERE client_id_idx = @svcIdx)
            """, new { tenantId, svc, svcIdx }, tx);

        var authorizations = conn.Execute(
            """
            DELETE FROM openiddict_authorizations
            WHERE subject IN (SELECT id FROM users WHERE tenant_id = @tenantId)
               OR subject = @svc
               OR application_id IN (SELECT id FROM openiddict_applications
                                      WHERE client_id_idx = @svcIdx)
            """, new { tenantId, svc, svcIdx }, tx);

        var clients = conn.Execute(
            "DELETE FROM openiddict_applications WHERE client_id_idx = @svcIdx",
            new { svcIdx }, tx);

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
        // while carrying no credential and no PII-class field. The values
        // are synthetic (not PII) but still encrypt under the cp domain so
        // classified columns hold ciphertext only, and `email_idx` moves
        // with the value to keep the unique index consistent.
        var userIds = conn.Query<string>(
            "SELECT id FROM users WHERE tenant_id = @tenantId",
            new { tenantId }, tx).ToList();
        var users = 0;
        foreach (var userId in userIds)
        {
            var tombstone = $"erased-{userId}@erased";
            var emailCt = await CpCrypto.EncryptGlobalAsync(cipher, tombstone, ct);
            var nameCt = await CpCrypto.EncryptGlobalAsync(cipher, "erased", ct);
            var emailIdx = await CpCrypto.GlobalIndexAsync(
                index, PiiClass.Contact, tombstone, ct);
            users += conn.Execute(
                """
                UPDATE users
                SET email          = @emailCt,
                    email_idx      = @emailIdx,
                    display_name   = @nameCt,
                    password_hash  = NULL,
                    mfa_enabled    = 0,
                    mfa_secret_ref = NULL,
                    org_name       = NULL,
                    status         = 'disabled',
                    updated_utc    = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                WHERE id = @userId
                """, new { userId, emailCt, emailIdx, nameCt }, tx);
        }

        tx.Commit();
        return new ResidualCleanup(
            tokens, authorizations, clients, invites, groups, users);
    }
}
