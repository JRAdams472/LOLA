using System.Text;
using Dapper;
using Lola.Application.Audit;
using Lola.Application.Security;
using Lola.ControlPlane;
using Lola.Domain.Classification;
using Lola.Tools.CpConvert;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Lola.ControlPlane.Tests;

/// MPE-287 rehearsal on disposable fixtures: unkeyed source → keyed
/// dest conversion, resume-after-interruption, dup refusal, verbatim
/// audit chain, marker scan, rekey round-trip, custody backup.
public sealed class CpConvertTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cpconv-" + Guid.NewGuid().ToString("N"));
    private const string Key = TestDbKeys.ControlFileKeyHex;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        // Best-effort — Windows can briefly hold -wal/-shm handles.
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public async Task Convert_produces_keyed_ciphertext_indexes_and_working_lookup()
    {
        var (src, dst) = Paths();
        SeedSource(src);
        var result = await Converter().ConvertAsync(src, dst, Key);

        result.AuditChainOk.ShouldBeTrue();
        result.RoundTripOk.ShouldBeTrue();
        result.ForeignKeysOk.ShouldBeTrue();
        result.RowCounts["users"].ShouldBe(3);
        File.Exists(src + ".cpbak").ShouldBeTrue(); // custody backup

        // Unkeyed open of the keyed dest fails — ciphertext at rest.
        Should.Throw<SqliteException>(() =>
        {
            using var c = new SqliteConnection($"Data Source={dst};Pooling=false");
            c.Open();
            c.ExecuteScalar("SELECT COUNT(*) FROM users");
        });

        // Directory reads through the same crypto boundary resolve.
        var db = new ControlPlaneDb(dst, Key);
        var users = new UserDirectory(db, TestCrypto.Cipher, TestCrypto.Index);
        var found = await users.FindByEmailInTenantAsync("t1", "p@x.dev");
        found.ShouldNotBeNull();
        found!.Email.ShouldBe("p@x.dev");
    }

    [Fact]
    public async Task Resume_after_interruption_skips_committed_tables()
    {
        var (src, dst) = Paths();
        SeedSource(src);

        // First run dies on the users-table marker value.
        var killer = new KillCipher(TestCrypto.Cipher, "die@x.dev");
        var conv1 = new CpConverter(killer, TestCrypto.Index);
        await Should.ThrowAsync<InvalidOperationException>(
            () => conv1.ConvertAsync(src, dst, Key));

        var done = await ReadStateAsync(dst);
        done.ShouldNotBeEmpty(); // earlier tables committed

        var result = await Converter().ConvertAsync(src, dst, Key);
        result.Resumed.ShouldNotBeEmpty();
        result.RoundTripOk.ShouldBeTrue();

        var db = new ControlPlaneDb(dst, Key);
        using var conn = db.Open();
        // No double-committed rows after resume.
        (await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM users")).ShouldBe(3);
        (await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM tenants")).ShouldBe(1);
    }

    [Fact]
    public async Task Duplicate_tenant_scoped_email_refuses_no_partial_switch()
    {
        var (src, dst) = Paths();
        SeedSource(src, dupEmail: true);
        await Should.ThrowAsync<Exception>(
            () => Converter().ConvertAsync(src, dst, Key));
    }

    [Fact]
    public async Task Marker_scan_finds_no_plaintext_in_keyed_artifacts()
    {
        var (src, dst) = Paths();
        SeedSource(src);
        await Converter().ConvertAsync(src, dst, Key);
        using (var c = new ControlPlaneDb(dst, Key).Open())
            await c.ExecuteAsync("PRAGMA wal_checkpoint(TRUNCATE)");

        // Source plaintext file still carries the marker — conversion
        // does not retro-protect the legacy artifact (custody §7).
        Encoding.ASCII.GetString(File.ReadAllBytes(src))
            .ShouldContain("marker-9x7@test.dev");
        var destBytes = File.ReadAllBytes(dst);
        var wal = dst + "-wal";
        if (File.Exists(wal))
            destBytes = destBytes.Concat(File.ReadAllBytes(wal)).ToArray();
        Encoding.ASCII.GetString(destBytes)
            .ShouldNotContain("marker-9x7@test.dev");
    }

    [Fact]
    public async Task Rekey_round_trip_old_key_fails_new_key_opens()
    {
        var (src, dst) = Paths();
        SeedSource(src);
        await Converter().ConvertAsync(src, dst, Key);

        var newKey = new string('A', 64);
        using (var conn = new ControlPlaneDb(dst, Key).Open())
            await conn.ExecuteAsync($"PRAGMA rekey = \"x'{newKey}'\"");

        Should.Throw<SqliteException>(() =>
        {
            using var old = new ControlPlaneDb(dst, Key).Open();
            old.ExecuteScalar("SELECT COUNT(*) FROM users");
        });
        using var fresh = new ControlPlaneDb(dst, newKey).Open();
        (await fresh.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM users")).ShouldBe(3);
    }

    private static CpConverter Converter()
        => new(TestCrypto.Cipher, TestCrypto.Index);

    private (string Src, string Dst) Paths()
    {
        Directory.CreateDirectory(_dir);
        return (Path.Combine(_dir, "control-src.db"),
                Path.Combine(_dir, "control-dst.db"));
    }

    /// Builds a pre-7c-style source: full migrated schema, PLAINTEXT
    /// classified columns, NULL *_idx — what a legacy control.db holds.
    private void SeedSource(string src, bool dupEmail = false)
    {
        ControlPlaneMigrator.Migrate($"Data Source={src}");
        SqliteConnection.ClearAllPools(); // DbUp pools — release the file
        using var conn = new SqliteConnection(
            $"Data Source={src};Pooling=false");
        conn.Open();
        conn.Execute(
            "INSERT INTO tenants (id, slug, display_name, status, db_path) " +
            "VALUES ('t1', 'acme', 'Acme Corp', 'active', 'data/t1.db')");
        conn.Execute(
            "INSERT INTO users (id, tenant_id, email, display_name, " +
            "password_hash, status, mfa_enabled, created_utc) VALUES " +
            "('u1','t1','p@x.dev','Pat X','h','active',0,'2026-01-01')");
        conn.Execute(
            "INSERT INTO users (id, tenant_id, email, display_name, " +
            "password_hash, status, mfa_enabled, created_utc) VALUES " +
            "('u2','t1','die@x.dev','Die','h','active',0,'2026-01-01')");
        if (dupEmail)
            conn.Execute(
                "INSERT INTO users (id, tenant_id, email, display_name, " +
                "password_hash, status, mfa_enabled, created_utc) VALUES " +
                "('u3','t1','P@X.dev','Dup','h','active',0,'2026-01-01')");
        conn.Execute(
            "INSERT INTO users (id, tenant_id, email, display_name, " +
            "password_hash, status, mfa_enabled, created_utc) VALUES " +
            "('u9','t1','marker-9x7@test.dev','Marker','h','active',0,'2026-01-01')");
        conn.Execute(
            "INSERT INTO groups (id, tenant_id, name) VALUES " +
            "('g1','t1','officers')");
        conn.Execute(
            "INSERT INTO openiddict_applications " +
            "(id, client_id, client_secret) VALUES ('a1','svc-t1','sec')");
        // Two chained audit events — copied verbatim, reverified.
        var h1 = HashChain.ComputeHash(HashChain.Genesis, "e1", "a",
            "seed", "tenant", "t1", [], null, "2026-01-01T00:00:00Z");
        var h2 = HashChain.ComputeHash(h1, "e2", "a",
            "seed", "tenant", "t1", [], null, "2026-01-01T00:00:01Z");
        conn.Execute(
            "INSERT INTO audit_events (seq, event_id, actor, action, " +
            "entity, entity_id, field_classes, reason, prev_hash, hash, " +
            "created_utc) VALUES (1,'e1','a','seed','tenant','t1','[]'," +
            "NULL,@gen,@h1,'2026-01-01T00:00:00Z')," +
            "(2,'e2','a','seed','tenant','t1','[]',NULL,@h1,@h2," +
            "'2026-01-01T00:00:01Z')", new { gen = HashChain.Genesis, h1, h2 });
    }

    private async Task<List<string>> ReadStateAsync(string dst)
    {
        using var conn = (SqliteConnection)
            ControlKeyedConnectionManager.OpenKeyed(dst, Key);
        return (await conn.QueryAsync<string>(
            "SELECT table_name FROM cpconvert_state WHERE done = 1"))
            .ToList();
    }

    /// Deterministic-kill cipher — throws on a marker plaintext so the
    /// first conversion run dies mid-stream, proving resume.
    private sealed class KillCipher(IFieldCipher inner, string killOn)
        : IFieldCipher
    {
        public Task<string> EncryptAsync(string p, CancellationToken ct)
            => p == killOn
                ? throw new InvalidOperationException("killed mid-table")
                : inner.EncryptAsync(p, ct);
        public Task<IReadOnlyList<string>> EncryptAsync(
            IReadOnlyList<string> p, CancellationToken ct)
            => inner.EncryptAsync(p, ct);
        public Task<string> DecryptAsync(string c, CancellationToken ct)
            => inner.DecryptAsync(c, ct);
        public Task<IReadOnlyList<string>> DecryptAsync(
            IReadOnlyList<string> c, CancellationToken ct)
            => inner.DecryptAsync(c, ct);
    }
}
