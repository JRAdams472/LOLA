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
            Path.Combine(Path.GetTempPath(), "lola-tests", $"mig2-{Guid.NewGuid():N}.db"));
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
}
