using Dapper;
using Lola.Application.Identity;
using Lola.ControlPlane;
using Shouldly;

namespace Lola.ControlPlane.Tests;

/// MPE-108/119 residual lifecycle on a real control-plane DB: a pass
/// deletes invites, OIDC tokens/authorizations, the svc-{tenant} client,
/// and the group structure; anonymizes users; and retains the audit
/// spine (tenants row, tenant_migrations, audit_events). A second pass
/// is a no-op — erase retries converge.
public class ResidualCleanupTests
{
    private static ControlPlaneDb TempDb()
    {
        var db = new ControlPlaneDb(Path.Combine(
            Path.GetTempPath(), "lola-tests", $"res-{Guid.NewGuid():N}.db"), TestDbKeys.ControlFileKeyHex);
        db.Migrate();
        return db;
    }

    [Fact]
    public async Task Clean_removes_credentials_and_keeps_spine()
    {
        var db = TempDb();
        var tid = $"t{Guid.NewGuid():N}"[..12];
        using (var conn = db.Open())
        {
            conn.Execute(
                "INSERT INTO tenants (id, slug, slug_idx, display_name, status, db_path) " +
                "VALUES (@tid, @s, @si, @n, 'active', 'x.db')",
                new { tid, s = TestCrypto.Enc(tid), si = TestCrypto.Idx(tid), n = TestCrypto.Enc("T") });
            conn.Execute(
                """
                INSERT INTO users (id, tenant_id, email, email_idx, display_name, password_hash, status)
                VALUES ('u1', @tid, @e1, @i1, @n1, 'phc$argon2', 'active'),
                       ('u2', @tid, @e2, @i2, @n2, NULL, 'invited'),
                       ('u3', @tid, @e3, @i3, @n3, 'phc$argon2', 'active')
                """, new
                {
                    tid,
                    e1 = TestCrypto.Enc("a@x.dev"),
                    i1 = TestCrypto.Idx("a@x.dev"),
                    n1 = TestCrypto.Enc("Alice"),
                    e2 = TestCrypto.Enc("b@x.dev"),
                    i2 = TestCrypto.Idx("b@x.dev"),
                    n2 = TestCrypto.Enc("Bob"),
                    e3 = TestCrypto.Enc("p@x.dev"),
                    i3 = TestCrypto.Idx("p@x.dev"),
                    n3 = TestCrypto.Enc("Partner")
                });
            // Invited partner carries a synthetic org attribution
            // (M19) — erasure must drop it like every other
            // PII-class field (MPE-273).
            conn.Execute(
                "UPDATE users SET org_name = @o WHERE id = 'u3'",
                new { o = TestCrypto.Enc("TPO Org LLC") });
            conn.Execute(
                "INSERT INTO groups (id, tenant_id, name, name_idx) " +
                "VALUES ('g1', @tid, @n, @ni)",
                new { tid, n = TestCrypto.Enc("staff"), ni = TestCrypto.Idx("staff") });
            conn.Execute(
                "INSERT INTO group_roles (group_id, role) VALUES ('g1', 'LoanOfficer')");
            conn.Execute(
                "INSERT INTO user_groups (user_id, group_id) VALUES ('u1', 'g1')");
            conn.Execute(
                """
                INSERT INTO invites (id, tenant_id, email, email_idx, role, token_hash, expires_utc, created_by)
                VALUES ('i1', @tid, @e, @ei, 'Borrower', 'hash1', '2030-01-01', 'u1')
                """, new { tid, e = TestCrypto.Enc("new@x.dev"), ei = TestCrypto.Idx("new@x.dev") });
            conn.Execute(
                "INSERT INTO openiddict_applications (id, client_id, client_id_idx, client_secret) " +
                "VALUES ('app1', @c, @ci, 'hashed')",
                new { c = TestCrypto.Enc($"svc-{tid}"), ci = TestCrypto.Idx($"svc-{tid}") });
            conn.Execute(
                """
                INSERT INTO openiddict_authorizations (id, application_id, subject)
                VALUES ('az1', 'app1', 'u1'), ('az2', NULL, @svc)
                """, new { svc = $"svc-{tid}" });
            conn.Execute(
                """
                INSERT INTO openiddict_tokens (id, application_id, authorization_id, subject)
                VALUES ('tk1', 'app1', 'az1', 'u1'),
                       ('tk2', 'app1', NULL, @svc),
                       ('tk3', NULL, NULL, 'u2')
                """, new { svc = $"svc-{tid}" });
            // The spine: migration rollout + a surviving audit event.
            conn.Execute(
                "INSERT INTO tenant_migrations (tenant_id, version, name, status) " +
                "VALUES (@tid, 1, '0001_baseline', 'applied')", new { tid });
            conn.Execute(
                """
                INSERT INTO audit_events (event_id, actor, action, entity, entity_id,
                    field_classes, prev_hash, hash, created_utc)
                VALUES ('e1', 'op', 'retention_override', 'tenant', @tid,
                    '[]', 'GENESIS', 'h1', '2026-01-01T00:00:00Z')
                """, new { tid });
        }

        var result = await new ControlPlaneResiduals(db, TestCrypto.Cipher, TestCrypto.Index).CleanAsync(tid);
        result.ShouldBe(new ResidualCleanup(3, 2, 1, 1, 1, 3));

        using (var conn = db.Open())
        {
            // Credentials and identity structures are gone.
            conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM openiddict_tokens").ShouldBe(0);
            conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM openiddict_authorizations").ShouldBe(0);
            conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM openiddict_applications").ShouldBe(0);
            conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM invites WHERE tenant_id = @tid",
                new { tid }).ShouldBe(0);
            conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM groups WHERE tenant_id = @tid",
                new { tid }).ShouldBe(0);
            conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM user_groups").ShouldBe(0);
            conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM group_roles").ShouldBe(0);

            // Users are tombstones: no credential, no PII-class field.
            var users = conn.Query<(
                string Id, string Email, string Display, string? Hash, string Status,
                long Mfa, string? MfaRef, string? Org)>(
                """
                SELECT id AS Id, email AS Email, display_name AS Display,
                       password_hash AS Hash, status AS Status,
                       mfa_enabled AS Mfa, mfa_secret_ref AS MfaRef,
                       org_name AS Org
                FROM users WHERE tenant_id = @tid ORDER BY id
                """, new { tid }).ToList();
            users.Count.ShouldBe(3);
            foreach (var u in users)
            {
                // Tombstones are cp-domain ciphertext — the column
                // still holds no plaintext marker (MPE-286).
                u.Email.ShouldBe(TestCrypto.Enc($"erased-{u.Id}@erased"));
                u.Display.ShouldBe(TestCrypto.Enc("erased"));
                u.Hash.ShouldBeNull();
                u.Status.ShouldBe("disabled");
                u.Mfa.ShouldBe(0);
                u.MfaRef.ShouldBeNull();
                u.Org.ShouldBeNull();
            }

            // The audit spine survives untouched.
            conn.ExecuteScalar<string>(
                "SELECT status FROM tenants WHERE id = @tid",
                new { tid }).ShouldBe("active"); // residual pass does NOT retire
            conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM tenant_migrations WHERE tenant_id = @tid",
                new { tid }).ShouldBe(1);
            conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM audit_events WHERE entity_id = @tid",
                new { tid }).ShouldBe(1);
        }

        // Idempotent: a retried erase's second pass finds nothing.
        var again = await new ControlPlaneResiduals(db, TestCrypto.Cipher, TestCrypto.Index).CleanAsync(tid);
        again.ShouldBe(new ResidualCleanup(0, 0, 0, 0, 0, 3));
    }
}
