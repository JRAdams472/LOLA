using Dapper;
using Lola.ControlPlane;
using Shouldly;

namespace Lola.ControlPlane.Tests;

/// 0002_identity.sql — the identity/group/invite/openiddict schema.
public class IdentityMigrationTests
{
    private static ControlPlaneDb TempDb()
    {
        var db = new ControlPlaneDb(
            Path.Combine(Path.GetTempPath(), "lola-tests", $"mig2-{Guid.NewGuid():N}.db"), TestDbKeys.ControlFileKeyHex);
        db.Migrate();
        return db;
    }

    [Fact]
    public void Migrate_creates_identity_tables()
    {
        using var conn = TempDb().Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table'").ToHashSet();
        foreach (var t in new[]
                 {
                     "users", "groups", "group_roles", "user_groups",
                     "role_visibility", "invites",
                     "openiddict_applications", "openiddict_authorizations",
                     "openiddict_scopes", "openiddict_tokens"
                     ,
                 })
            tables.ShouldContain(t);
    }

    [Fact]
    public void Migrate_adds_wrapped_key_column()
    {
        using var conn = TempDb().Open();
        var cols = conn.Query<string>("SELECT name FROM pragma_table_info('tenants')").ToList();
        cols.ShouldContain("db_key_wrapped");
    }

    [Fact]
    public void Role_visibility_seed_matches_locked_map()
    {
        using var conn = TempDb().Open();
        var rows = conn.Query<(string role, string pii_class, string access)>(
            "SELECT role, pii_class, access FROM role_visibility").ToList();

        // TenantAdmin: plaintext on all 8 classes.
        rows.Count(r => r.role == "TenantAdmin" && r.access == "plaintext").ShouldBe(8);

        // LoanOfficer: working fields only — sensitive classes absent (masked).
        var officer = rows.Where(r => r.role == "LoanOfficer").Select(r => r.pii_class).ToHashSet();
        officer.ShouldBe(new HashSet<string> { "Name", "Address", "Contact", "Income", "Other" });

        // Borrower/Service: no rows at all — masked by default (ADR-0008).
        rows.Any(r => r.role is "Borrower" or "Service").ShouldBeFalse();
    }

    [Fact]
    public void Invites_store_hash_only()
    {
        using var conn = TempDb().Open();
        var cols = conn.Query<string>("SELECT name FROM pragma_table_info('invites')").ToList();
        cols.ShouldContain("token_hash");
        cols.ShouldNotContain("token"); // the bearer secret never rests in the DB
    }
    // ---- 0008_tpo.sql (MPE-265) ----

    [Fact]
    public void Tpo_adds_org_name_to_users_and_invites()
    {
        using var conn = TempDb().Open();
        conn.Query<string>("SELECT name FROM pragma_table_info('users')")
            .ShouldContain("org_name");
        conn.Query<string>("SELECT name FROM pragma_table_info('invites')")
            .ShouldContain("org_name");
    }

    [Fact]
    public void Tpo_partner_roles_pass_every_check_but_visibility()
    {
        using var conn = TempDb().Open();
        conn.Execute("INSERT INTO tenants (id, slug, display_name,"
            + " status, db_path) VALUES ('t1', 't1', 'T', 'active',"
            + " 'x.db')");
        conn.Execute(
            "INSERT INTO users (id, tenant_id, email, display_name,"
            + " org_name, status, created_utc)"
            + " VALUES ('u-tpo', 't1', 'b@tpo', 'B', 'TPO LLC',"
            + " 'invited', '2026-11-20T00:00:00Z')");
        conn.Execute(
            "INSERT INTO groups (id, tenant_id, name)"
            + " VALUES ('g-tpo', 't1', 'brokers')");
        // group_roles admits the partner roles.
        conn.Execute("INSERT INTO group_roles (group_id, role)"
            + " VALUES ('g-tpo', 'Broker'), ('g-tpo', 'Realtor')");
        // invites CHECK admits partner roles + org.
        conn.Execute(
            "INSERT INTO invites (id, tenant_id, email, role,"
            + " token_hash, expires_utc, created_by, org_name)"
            + " VALUES ('i-tpo', 't1', 'b@tpo', 'Broker', 'h',"
            + " '2099-01-01T00:00:00Z', 'u-admin', 'TPO LLC')");
        // role_visibility stays 4-role — partners are masked by
        // construction and the CHECK refuses a grant outright.
        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() =>
            conn.Execute(
                "INSERT INTO role_visibility"
                + " (role, pii_class, access)"
                + " VALUES ('Broker', 'Identity', 'masked')"));
    }
}
