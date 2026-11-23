using Dapper;
using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.ControlPlane;
using Shouldly;

namespace Lola.ControlPlane.Tests;

/// MPE-273 acceptance on migrated real SQLite: the invite acceptance
/// port resolves the protected system group, inserts the user and
/// membership, and consumes the single-use invite inside ONE
/// BEGIN IMMEDIATE transaction — a lost race or failed predicate
/// rolls every write back.
public class InviteAcceptanceTests
{
    private static ControlPlaneDb TempDb()
    {
        var db = new ControlPlaneDb(Path.Combine(
            Path.GetTempPath(), "lola-tests", $"acc-{Guid.NewGuid():N}.db"), TestDbKeys.ControlFileKeyHex);
        db.Migrate();
        return db;
    }

    private static void SeedTenant(ControlPlaneDb db, string tid)
    {
        using var conn = db.Open();
        conn.Execute(
            "INSERT INTO tenants (id, slug, slug_idx, display_name, status, db_path) " +
            "VALUES (@tid, @s, @si, @n, 'active', 'x.db')",
            new { tid, s = TestCrypto.Enc(tid), si = TestCrypto.Idx(tid), n = TestCrypto.Enc("T") });
    }

    private static void SeedInvite(ControlPlaneDb db, string tid,
        string id, string hash, string role = "Broker",
        string? org = "TPO Org", string? used = null,
        string expires = "2099-01-01T00:00:00Z", string email = "p@x")
    {
        using var conn = db.Open();
        conn.Execute("""
            INSERT INTO invites (id, tenant_id, email, email_idx, role, org_name,
                                 token_hash, expires_utc, used_utc,
                                 created_by)
            VALUES (@id, @tid, @email, @emailIdx, @role, @org, @hash, @expires,
                    @used, 'u-admin')
            """, new
        {
            id,
            tid,
            email = TestCrypto.Enc(email),
            emailIdx = TestCrypto.Idx(email),
            role,
            org = org is null ? null : TestCrypto.Enc(org),
            hash,
            expires,
            used
        });
    }

    private static string HashOf(string token)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.ASCII.GetBytes(token)))
            .ToLowerInvariant();

    [Fact]
    public async Task Accept_creates_user_membership_and_protected_group_atomically()
    {
        var db = TempDb();
        SeedTenant(db, "t1");
        SeedInvite(db, "t1", "i1", HashOf("tok"));

        var outcome = await new InviteAcceptance(db, TestCrypto.Cipher, TestCrypto.Index).AcceptAsync(
            new InviteAcceptanceRequest(HashOf("tok"), "Partner", "phc$"));

        using var conn = db.Open();
        var user = conn.QuerySingle<dynamic>(
            "SELECT * FROM users WHERE id = @id",
            new { id = outcome.UserId });
        // Stored columns are ciphertext + keyed index — never the
        // plaintext marker (MPE-286).
        ((string)user.email).ShouldBe(TestCrypto.Enc("p@x"));
        ((string)user.email_idx).ShouldBe(TestCrypto.Idx("p@x"));
        ((string)user.org_name).ShouldBe(TestCrypto.Enc("TPO Org"));
        ((string)user.status).ShouldBe("active");
        ((string)user.display_name).ShouldBe(TestCrypto.Enc("Partner"));

        var grp = conn.QuerySingle<dynamic>(
            "SELECT * FROM groups WHERE tenant_id = 't1'");
        ((string?)grp.system_kind).ShouldBe("broker");
        ((string)grp.name).ShouldBe(TestCrypto.Enc("brokers"));
        ((string)grp.name_idx).ShouldBe(TestCrypto.Idx("brokers"));
        conn.Query<string>(
            "SELECT role FROM group_roles WHERE group_id = @id",
            new { id = (string)grp.id })
            .ShouldBe(["Broker"]);
        conn.ExecuteScalar<int>(
            "SELECT count(*) FROM user_groups " +
            "WHERE user_id = @u AND group_id = @g",
            new { u = outcome.UserId, g = (string)grp.id }).ShouldBe(1);
        conn.ExecuteScalar<string?>(
            "SELECT used_utc FROM invites WHERE id = 'i1'")
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task Concurrent_accepts_of_one_invite_leave_exactly_one_account()
    {
        var db = TempDb();
        SeedTenant(db, "t1");
        SeedInvite(db, "t1", "i1", HashOf("tok"));
        var acceptance = new InviteAcceptance(db, TestCrypto.Cipher, TestCrypto.Index);

        var results = await Task.WhenAll(
            TryAccept(acceptance, "tok", "A"),
            TryAccept(acceptance, "tok", "B"));

        results.Count(r => r.Ok).ShouldBe(1);
        results.Count(r => !r.Ok).ShouldBe(1);
        using var conn = db.Open();
        conn.ExecuteScalar<int>("SELECT count(*) FROM users").ShouldBe(1);
        conn.ExecuteScalar<int>("SELECT count(*) FROM user_groups")
            .ShouldBe(1);
        conn.ExecuteScalar<int>(
            "SELECT count(*) FROM groups WHERE system_kind IS NOT NULL")
            .ShouldBe(1);
        conn.ExecuteScalar<string?>(
            "SELECT used_utc FROM invites WHERE id = 'i1'")
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task Different_first_invites_converge_on_one_protected_group()
    {
        var db = TempDb();
        SeedTenant(db, "t1");
        SeedInvite(db, "t1", "i1", HashOf("t-a"), email: "a@x");
        SeedInvite(db, "t1", "i2", HashOf("t-b"), email: "b@x");
        var acceptance = new InviteAcceptance(db, TestCrypto.Cipher, TestCrypto.Index);

        var results = await Task.WhenAll(
            TryAccept(acceptance, "t-a", "A"),
            TryAccept(acceptance, "t-b", "B"));

        results.Count(r => r.Ok).ShouldBe(2);
        using var conn = db.Open();
        conn.ExecuteScalar<int>(
            "SELECT count(*) FROM groups WHERE system_kind = 'broker'")
            .ShouldBe(1);
        conn.ExecuteScalar<int>("SELECT count(*) FROM user_groups")
            .ShouldBe(2);
    }

    [Fact]
    public async Task Reserved_alias_squatter_blocks_acceptance_with_no_writes()
    {
        var db = TempDb();
        SeedTenant(db, "t1");
        SeedInvite(db, "t1", "i1", HashOf("tok"), role: "Borrower");
        using (var conn = db.Open())
        {
            conn.Execute("INSERT INTO groups (id, tenant_id, name, name_idx) " +
                "VALUES ('g-evil', 't1', @n, @ni)",
                new { n = TestCrypto.Enc("borrowers"), ni = TestCrypto.Idx("borrowers") });
            conn.Execute("INSERT INTO group_roles (group_id, role) " +
                "VALUES ('g-evil', 'LoanOfficer')");
        }

        var ex = await Should.ThrowAsync<DomainException>(() =>
            new InviteAcceptance(db, TestCrypto.Cipher, TestCrypto.Index).AcceptAsync(
                new InviteAcceptanceRequest(HashOf("tok"), "P", "phc$")));
        ex.Status.ShouldBe(409);

        using var conn2 = db.Open();
        conn2.ExecuteScalar<int>("SELECT count(*) FROM users")
            .ShouldBe(0);
        conn2.ExecuteScalar<int>("SELECT count(*) FROM user_groups")
            .ShouldBe(0);
        conn2.ExecuteScalar<string?>(
            "SELECT used_utc FROM invites WHERE id = 'i1'")
            .ShouldBeNull();
        conn2.ExecuteScalar<string?>(
            "SELECT system_kind FROM groups WHERE id = 'g-evil'")
            .ShouldBeNull();
    }

    [Fact]
    public async Task Drifted_protected_group_blocks_acceptance()
    {
        var db = TempDb();
        SeedTenant(db, "t1");
        SeedInvite(db, "t1", "i1", HashOf("tok"), role: "Broker");
        using (var conn = db.Open())
        {
            conn.Execute("INSERT INTO groups (id, tenant_id, name, name_idx, " +
                "system_kind) VALUES ('g-b', 't1', @n, @ni, 'broker')",
                new { n = TestCrypto.Enc("brokers"), ni = TestCrypto.Idx("brokers") });
            conn.Execute("INSERT INTO group_roles (group_id, role) " +
                "VALUES ('g-b', 'LoanOfficer')");
        }

        var ex = await Should.ThrowAsync<DomainException>(() =>
            new InviteAcceptance(db, TestCrypto.Cipher, TestCrypto.Index).AcceptAsync(
                new InviteAcceptanceRequest(HashOf("tok"), "P", "phc$")));
        ex.Status.ShouldBe(409);
        using var conn2 = db.Open();
        conn2.ExecuteScalar<int>("SELECT count(*) FROM users")
            .ShouldBe(0);
        conn2.ExecuteScalar<string?>(
            "SELECT used_utc FROM invites WHERE id = 'i1'")
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("Borrower", "used")]
    [InlineData("Borrower", "expired")]
    [InlineData("TenantAdmin", null)]
    public async Task Invalid_used_expired_or_staff_invites_fail_without_writes(
        string role, string? state)
    {
        var db = TempDb();
        SeedTenant(db, "t1");
        SeedInvite(db, "t1", "i1", HashOf("tok"), role: role,
            used: state == "used" ? "2030-01-01T00:00:00Z" : null,
            expires: state == "expired"
                ? "2000-01-01T00:00:00Z" : "2099-01-01T00:00:00Z");

        var ex = await Should.ThrowAsync<DomainException>(() =>
            new InviteAcceptance(db, TestCrypto.Cipher, TestCrypto.Index).AcceptAsync(
                new InviteAcceptanceRequest(HashOf("tok"), "P", "phc$")));
        ex.Status.ShouldBe(400);
        using var conn = db.Open();
        conn.ExecuteScalar<int>("SELECT count(*) FROM users")
            .ShouldBe(0);
        conn.ExecuteScalar<int>(
            "SELECT count(*) FROM groups WHERE system_kind IS NOT NULL")
            .ShouldBe(0);
    }

    [Fact]
    public async Task Unknown_token_and_duplicate_email_fail_without_writes()
    {
        var db = TempDb();
        SeedTenant(db, "t1");
        SeedInvite(db, "t1", "i1", HashOf("tok"));

        var ex = await Should.ThrowAsync<DomainException>(() =>
            new InviteAcceptance(db, TestCrypto.Cipher, TestCrypto.Index).AcceptAsync(
                new InviteAcceptanceRequest(HashOf("other"), "P", "phc$")));
        ex.Status.ShouldBe(400);

        using (var conn = db.Open())
            conn.Execute("INSERT INTO users (id, tenant_id, email, email_idx, " +
                "display_name, status) VALUES ('u-x', 't1', @e, @ei, " +
                "@n, 'active')",
                new { e = TestCrypto.Enc("p@x"), ei = TestCrypto.Idx("p@x"), n = TestCrypto.Enc("X") });
        var ex2 = await Should.ThrowAsync<DomainException>(() =>
            new InviteAcceptance(db, TestCrypto.Cipher, TestCrypto.Index).AcceptAsync(
                new InviteAcceptanceRequest(HashOf("tok"), "P", "phc$")));
        ex2.Status.ShouldBe(400);
        using var conn2 = db.Open();
        conn2.ExecuteScalar<string?>(
            "SELECT used_utc FROM invites WHERE id = 'i1'")
            .ShouldBeNull();
        conn2.ExecuteScalar<int>(
            "SELECT count(*) FROM users WHERE tenant_id = 't1'")
            .ShouldBe(1);
    }

    [Fact]
    public void Backfill_claims_exact_singleton_and_leaves_conflicts()
    {
        // Real 0009 SQL against a pre-migration DB: apply embedded
        // scripts 0001–0008, seed legacy groups, run 0009's text.
        var path = Path.Combine(Path.GetTempPath(), "lola-tests",
            $"bf-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var asm = typeof(ControlPlaneMigrator).Assembly;
        var scripts = asm.GetManifestResourceNames()
            .Where(n => n.Contains("Migrations.0"))
            .Order().ToList();
        scripts.Count.ShouldBeGreaterThanOrEqualTo(9);
        string Sql(string resourceName)
        {
            using var stream = asm.GetManifestResourceStream(resourceName)!;
            return new StreamReader(stream).ReadToEnd();
        }
        using (var conn = new ControlPlaneDb(path, TestDbKeys.ControlFileKeyHex).Open())
        {
            conn.Open();
            foreach (var n in scripts.Where(n => !n.Contains("0009")))
                conn.Execute(Sql(n));
            conn.Execute("INSERT INTO tenants (id, slug, display_name, " +
                "status, db_path) VALUES ('t1', 't1', 'T', 'active', 'x.db')");
            conn.Execute("""
                INSERT INTO groups (id, tenant_id, name) VALUES
                    ('g1', 't1', 'borrowers'),
                    ('g2', 't1', 'brokers'),
                    ('g3', 't1', 'realtors'),
                    ('g4', 't1', 'custom')
                """);
            conn.Execute("""
                INSERT INTO group_roles (group_id, role) VALUES
                    ('g1', 'Borrower'),
                    ('g2', 'LoanOfficer'),
                    ('g3', 'Realtor'), ('g3', 'Broker'),
                    ('g4', 'Borrower')
                """);
            conn.Execute(Sql(scripts.Single(n => n.Contains("0009"))));
        }

        using var check = new ControlPlaneDb(path, TestDbKeys.ControlFileKeyHex).Open();
        check.Query<(string id, string? kind)>(
                "SELECT id, system_kind AS kind FROM groups ORDER BY id")
            .ShouldBe(
            [
                ("g1", "borrower"),
                ("g2", null),
                ("g3", null),
                ("g4", null),
            ]);
    }

    private static async Task<(bool Ok, string? UserId)> TryAccept(
        IInviteAcceptance a, string token, string name)
    {
        try
        {
            var o = await a.AcceptAsync(new InviteAcceptanceRequest(
                HashOf(token), name, "phc$"));
            return (true, o.UserId);
        }
        catch (DomainException)
        {
            return (false, null);
        }
    }
}
