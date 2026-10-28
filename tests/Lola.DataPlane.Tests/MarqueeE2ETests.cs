using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Microsoft.Data.Sqlite;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-96 marquee e2e — the full tenant data-plane lifecycle in one
/// walk: provision → migration status → repository write/read → backup
/// → mutate → restore → verify → erase → keys+files gone + retired +
/// post-erase open fails. Real OpenBao + real SQLCipher throughout.
[Collection("openbao")]
public sealed class MarqueeE2ETests(OpenBaoFixture fixture)
{
    static MarqueeE2ETests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    [Fact]
    public async Task Provision_through_erasure_full_lifecycle()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"));
        db.Migrate();
        var registry = new TenantRegistry(db);
        var cipher = new TransitFieldCipher(fixture.Transit);
        var keys = new TransitKeyManager(fixture.Transit);
        var backupDir = Path.Combine(fixture.DataDir, "backups");
        var runner = new TenantMigrationRunner(
            cipher, registry, new TenantMigrationTracker(db));
        var svc = new TenantProvisioningService(
            registry, keys, new TenantDbProvisioner(cipher),
            new StubServiceClients(), runner, fixture.DataDir);
        var backup = new TenantBackupService(cipher, backupDir);
        var eraser = new TenantErasureService(keys, registry, backupDir);

        var id = fixture.NewTenant();
        await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
        var tenant = (await registry.FindByIdAsync(id))!;
        tenant.Status.ShouldBe("active");

        // Migration status — the repo seam reports baseline applied.
        using (TenantContext.Use(TenantScope.From(tenant)))
        {
            var info = await new SchemaInfoRepository(
                new TenantConnectionFactory(cipher)).GetAsync();
            info.Version.ShouldBe(1);
            info.AppliedMigrations.ShouldContain(
                n => n.EndsWith("0001_baseline.sql"));
        }

        // Repository write/read over the request-path connection.
        using (TenantContext.Use(TenantScope.From(tenant)))
        {
            using var conn = await new TenantConnectionFactory(cipher).OpenAsync();
            await conn.ExecuteAsync("CREATE TABLE _marquee (v TEXT)");
            await conn.ExecuteAsync("INSERT INTO _marquee VALUES ('original')");
            (await conn.QuerySingleAsync<string>("SELECT v FROM _marquee"))
                .ShouldBe("original");
        }

        // Backup → mutate → restore → original verified.
        var snap = await backup.BackupAsync(tenant);
        string keyHex;
        using (CipherContext.Use(id))
        {
            var b64 = await cipher.DecryptAsync(tenant.DbKeyWrapped!);
            keyHex = Convert.ToHexString(Convert.FromBase64String(b64));
        }
        using (var conn = KeyedSqliteConnectionManager.OpenKeyed(tenant.DbPath, keyHex))
            await conn.ExecuteAsync("INSERT INTO _marquee VALUES ('corrupted')");
        await backup.RestoreAsync(tenant, snap.Name);
        using (var conn = KeyedSqliteConnectionManager.OpenKeyed(tenant.DbPath, keyHex))
            (await conn.QueryAsync<string>("SELECT v FROM _marquee"))
                .ShouldBe(["original"]);

        // Erase — transit keys die first (invariant 9), then files.
        var result = await eraser.EraseAsync(tenant);
        result.FilesRemoved.ShouldBeGreaterThan(0);
        (await registry.FindByIdAsync(id))!.Status.ShouldBe("retired");
        File.Exists(tenant.DbPath).ShouldBeFalse();
        File.Exists(tenant.DbPath + "-wal").ShouldBeFalse();
        Directory.Exists(Path.Combine(backupDir, id)).ShouldBeFalse();

        // Post-erase: the wrapped file key can never unwrap again —
        // the transit key itself is gone, not just the file.
        await Should.ThrowAsync<Exception>(async () =>
        {
            using (CipherContext.Use(id))
                await cipher.DecryptAsync(tenant.DbKeyWrapped!);
        });

        // And nothing remains to open — a read-only open of the absent
        // file fails outright.
        Should.Throw<SqliteException>(() =>
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tenant.DbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            conn.Open();
        });

        // Re-erasure of a retired tenant is refused.
        (await Should.ThrowAsync<InvalidOperationException>(
            () => eraser.EraseAsync(tenant)))
            .Message.ShouldContain("retired");
    }
}
