using System.Reflection;
using System.Text;
using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Microsoft.Data.Sqlite;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-94 acceptance — the tenant repository seam and the isolation
/// proofs around it: wrong keys can't open a file, TenantContext is
/// the only way to reach a file, secure_delete leaves nothing
/// recoverable, and foreign_keys is actually enforced (not just set).
[Collection("openbao")]
public sealed class TenantRepositoryIsolationTests(OpenBaoFixture fixture)
{
    static TenantRepositoryIsolationTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private TenantRegistry registry = null!;
    private TenantMigrationTracker tracker = null!;
    private TransitFieldCipher cipher = null!;

    private void Init()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"));
        db.Migrate();
        registry = new TenantRegistry(db);
        tracker = new TenantMigrationTracker(db);
        cipher = new TransitFieldCipher(fixture.Transit);
    }

    private async Task<TenantRecord> ProvisionAsync(string id)
    {
        var svc = new TenantProvisioningService(
            registry, new TransitKeyManager(fixture.Transit),
            new TenantDbProvisioner(cipher), new StubServiceClients(),
            new TenantMigrationRunner(cipher, registry, tracker),
            fixture.DataDir);
        return (await svc.ProvisionAsync(new ProvisionRequest(id, id, id))).Tenant;
    }

    [Fact]
    public async Task Tenant_a_key_cannot_open_tenant_b_file()
    {
        Init();
        var a = fixture.NewTenant();
        var b = fixture.NewTenant();
        try
        {
            var aRec = await ProvisionAsync(a);
            var bRec = await ProvisionAsync(b);

            string aKeyHex;
            using (CipherContext.Use(aRec.Id))
            {
                var b64 = await cipher.DecryptAsync(aRec.DbKeyWrapped!);
                aKeyHex = Convert.ToHexString(Convert.FromBase64String(b64));
            }

            // B's file, A's key — SQLCipher must reject at first read.
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = bRec.DbPath,
                Pooling = false
            }.ToString());
            await using (conn)
            {
                conn.Open();
                conn.Execute($"PRAGMA key = \"x'{aKeyHex}'\";");
                var ex = Should.Throw<SqliteException>(
                    () => conn.ExecuteScalar<int>("SELECT count(*) FROM schema_info"));
                ex.Message.ToLowerInvariant().ShouldContain("not a database");
            }
        }
        finally
        {
            var km = new TransitKeyManager(fixture.Transit);
            await km.DestroyAsync(a); await km.DestroyAsync(b);
        }
    }

    [Fact]
    public async Task Context_a_reads_only_tenant_as_file()
    {
        Init();
        var a = fixture.NewTenant();
        var b = fixture.NewTenant();
        try
        {
            var aRec = await ProvisionAsync(a);
            var bRec = await ProvisionAsync(b);

            // Distinct marker rows in each file. A fresh factory per
            // scope — it caches the unwrapped key per instance by
            // design (one factory = one request = one tenant).
            foreach (var (rec, tag) in new[] { (aRec, "marker-a"), (bRec, "marker-b") })
            {
                using (TenantContext.Use(TenantScope.From(rec)))
                await using (var conn = await new TenantConnectionFactory(cipher).OpenAsync())
                {
                    conn.Execute(
                        "CREATE TABLE marker (tag TEXT NOT NULL);" +
                        "INSERT INTO marker (tag) VALUES (@tag)", new { tag });
                }
            }

            using (TenantContext.Use(TenantScope.From(aRec)))
            {
                var factory = new TenantConnectionFactory(cipher);
                var repo = new SchemaInfoRepository(factory);
                await using var conn = await factory.OpenAsync();

                // The connection is bound to A's file path, and sees
                // A's marker — B's data is unreachable by construction.
                var file = conn.Query<string>(
                    "SELECT file FROM pragma_database_list WHERE name = 'main'")
                    .Single();
                file.Replace('\\', '/').ShouldEndWith($"/{a}.db");
                conn.ExecuteScalar<string>("SELECT tag FROM marker")
                    .ShouldBe("marker-a");

                // The repository seam works end-to-end: schema_info +
                // DbUp journal from the scoped file.
                var info = await repo.GetAsync();
                info.Version.ShouldBe(1);
                info.AppliedMigrations.ShouldContain(n => n.Contains("0001_baseline"));
            }
        }
        finally
        {
            var km = new TransitKeyManager(fixture.Transit);
            await km.DestroyAsync(a); await km.DestroyAsync(b);
        }
    }

    [Fact]
    public void Data_plane_api_has_no_tenant_id_parameter()
    {
        // Structural proof (ADR-0003): nothing on the callable surface
        // of the connection factory or repository base can name a
        // tenant file — TenantContext is the only route in.
        var methods = typeof(TenantConnectionFactory)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Concat(typeof(TenantRepository).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            .Where(m => m.DeclaringType != typeof(object));
        var violations = methods
            .SelectMany(m => m.GetParameters()
                .Where(p => p.Name!.Contains("tenant", StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{m.DeclaringType!.Name}.{m.Name}({p.Name})"))
            .ToList();
        violations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tenant_file_holds_no_plaintext_even_before_delete()
    {
        Init();
        var id = fixture.NewTenant();
        try
        {
            var rec = await ProvisionAsync(id);
            var marker = $"LOLA-MARKER-{Guid.NewGuid():N}";
            var markerBytes = Encoding.ASCII.GetBytes(marker);

            using (TenantContext.Use(TenantScope.From(rec)))
            {
                await using (var conn = await new TenantConnectionFactory(cipher).OpenAsync())
                {
                    conn.Execute(
                        "CREATE TABLE secrets (v TEXT NOT NULL);" +
                        "INSERT INTO secrets (v) VALUES (@v)", new { v = marker });
                    // The row IS in the database — readable through SQL.
                    conn.ExecuteScalar<string>("SELECT v FROM secrets").ShouldBe(marker);

                    // On-disk scan while the WAL is live: the marker
                    // never exists as plaintext — SQLCipher encrypts
                    // the file AND its WAL. (invariant 1)
                    foreach (var path in Sidecars(rec.DbPath))
                    {
                        if (!File.Exists(path)) continue;
                        using var fs = new FileStream(path, FileMode.Open,
                            FileAccess.Read, FileShare.ReadWrite);
                        var bytes = new byte[fs.Length];
                        _ = fs.Read(bytes);
                        bytes.AsSpan().IndexOf(markerBytes)
                            .ShouldBeLessThan(0, $"plaintext at rest in {path}");
                    }
                }

                // delete + VACUUM + checkpoint — still nothing, now also
                // nothing recoverable from dropped pages.
                await using (var conn = await new TenantConnectionFactory(cipher).OpenAsync())
                {
                    conn.Execute("DELETE FROM secrets");
                    conn.Execute("VACUUM");
                    conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
                }
            }

            foreach (var path in Sidecars(rec.DbPath))
            {
                if (!File.Exists(path)) continue;
                File.ReadAllBytes(path).AsSpan().IndexOf(markerBytes)
                    .ShouldBeLessThan(0, $"plaintext recoverable in {path}");
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }

    [Fact]
    public void Secure_delete_zeroes_deleted_content()
    {
        // Plaintext control file — the only place secure_delete's effect
        // is visible to a byte-scan (the tenant file is encrypted).
        // Contrast proves the pragma does the work: OFF leaves deleted
        // bytes recoverable on the freed page, ON zeroes them at DELETE.
        // VACUUM is a separate cleanup path proven on the OFF file.
        Directory.CreateDirectory(fixture.DataDir);
        var marker = $"LOLA-MARKER-{Guid.NewGuid():N}";
        var markerBytes = Encoding.ASCII.GetBytes(marker);
        var scan = (string path) => File.ReadAllBytes(path).AsSpan().IndexOf(markerBytes);

        // secure_delete=OFF: deleted row bytes stay recoverable on the
        // freed page; VACUUM rebuilds the file and drops it.
        var offPath = Path.Combine(fixture.DataDir, $"off-{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={offPath};Pooling=false"))
        {
            conn.Open();
            conn.Execute("""
                PRAGMA secure_delete=OFF;
                CREATE TABLE secrets (v TEXT NOT NULL);
                INSERT INTO secrets (v) VALUES (@v);
                """, new { v = marker });
        }
        scan(offPath).ShouldBeGreaterThanOrEqualTo(0); // sanity: scan works

        using (var conn = new SqliteConnection($"Data Source={offPath};Pooling=false"))
        {
            conn.Open();
            conn.Execute("PRAGMA secure_delete=OFF; DELETE FROM secrets;");
        }
        scan(offPath).ShouldBeGreaterThanOrEqualTo(0,
            "control case failed — secure_delete=OFF should leave bytes");

        using (var conn = new SqliteConnection($"Data Source={offPath};Pooling=false"))
        {
            conn.Open();
            conn.Execute("VACUUM;");
        }
        scan(offPath).ShouldBeLessThan(0, "VACUUM should drop the free page");

        // secure_delete=ON: DELETE alone removes the bytes — no VACUUM
        // needed. This is the pragma every tenant connection sets.
        var onPath = Path.Combine(fixture.DataDir, $"on-{Guid.NewGuid():N}.db");
        using (var conn = new SqliteConnection($"Data Source={onPath};Pooling=false"))
        {
            conn.Open();
            conn.Execute("""
                PRAGMA secure_delete=ON;
                CREATE TABLE secrets (v TEXT NOT NULL);
                INSERT INTO secrets (v) VALUES (@v);
                """, new { v = marker });
        }
        scan(onPath).ShouldBeGreaterThanOrEqualTo(0);

        using (var conn = new SqliteConnection($"Data Source={onPath};Pooling=false"))
        {
            conn.Open();
            conn.Execute("PRAGMA secure_delete=ON; DELETE FROM secrets;");
        }
        scan(onPath).ShouldBeLessThan(0,
            "deleted plaintext recoverable with secure_delete=ON");
    }

    private static string[] Sidecars(string dbPath) =>
        [dbPath, dbPath + "-wal", dbPath + "-shm"];

    [Fact]
    public async Task Foreign_keys_are_enforced()
    {
        Init();
        var id = fixture.NewTenant();
        try
        {
            var rec = await ProvisionAsync(id);
            var factory = new TenantConnectionFactory(cipher);
            using (TenantContext.Use(TenantScope.From(rec)))
            await using (var conn = await factory.OpenAsync())
            {
                conn.Execute("""
                    CREATE TABLE parent (id INTEGER PRIMARY KEY);
                    CREATE TABLE child (
                        id  INTEGER PRIMARY KEY,
                        pid INTEGER NOT NULL REFERENCES parent(id));
                    """);
                conn.ExecuteScalar<long>("PRAGMA foreign_keys").ShouldBe(1);
                var ex = Should.Throw<SqliteException>(() =>
                    conn.Execute("INSERT INTO child (pid) VALUES (999)"));
                ex.SqliteErrorCode.ShouldBe(19); // SQLITE_CONSTRAINT
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }
}
