using System.Security.Cryptography;
using System.Text.Json;
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

/// MPE-95 acceptance: VACUUM INTO snapshots stay encrypted under the
/// tenant's file key (wrong/absent key reads them as not-a-database),
/// backup→mutate→restore returns the original bytes, and the sha256
/// manifest verifies. Real OpenBao + real SQLCipher — nothing faked.
[Collection("openbao")]
public sealed class TenantBackupTests(OpenBaoFixture fixture)
{
    static TenantBackupTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private (TenantBackupService backup, TenantProvisioningService svc,
             TenantRegistry registry, TransitFieldCipher cipher) Sut()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"));
        db.Migrate();
        var registry = new TenantRegistry(db);
        var cipher = new TransitFieldCipher(fixture.Transit);
        var runner = new TenantMigrationRunner(
            cipher, registry, new TenantMigrationTracker(db));
        var svc = new TenantProvisioningService(
            registry, new TransitKeyManager(fixture.Transit),
            new TenantDbProvisioner(cipher), new StubServiceClients(),
            runner, fixture.DataDir);
        var backup = new TenantBackupService(
            cipher, Path.Combine(fixture.DataDir, "backups"));
        return (backup, svc, registry, cipher);
    }

    private async Task<string> UnwrapAsync(TenantRecord tenant)
    {
        using (CipherContext.Use(tenant.Id))
        {
            var b64 = await new TransitFieldCipher(fixture.Transit)
                .DecryptAsync(tenant.DbKeyWrapped!);
            return Convert.ToHexString(Convert.FromBase64String(b64));
        }
    }

    /// A keyed read must die as "file is not a database" when the key
    /// is wrong or absent — PRAGMA key itself never fails; the first
    /// real read does.
    private static void AssertNotDatabase(string path, string? keyHex)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        conn.Open();
        if (keyHex is not null)
            conn.Execute($"PRAGMA key = \"x'{keyHex}'\"");
        var ex = Should.Throw<SqliteException>(
            () => conn.Execute("SELECT count(*) FROM sqlite_master"));
        ex.Message.ShouldContain("not a database", Case.Insensitive);
    }

    [Fact]
    public async Task Backup_mutate_restore_returns_original_data()
    {
        var (backup, svc, registry, _) = Sut();
        var id = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
            var tenant = (await registry.FindByIdAsync(id))!;
            var keyHex = await UnwrapAsync(tenant);

            using (var conn = KeyedSqliteConnectionManager.OpenKeyed(tenant.DbPath, keyHex))
            {
                await conn.ExecuteAsync("CREATE TABLE _marker (v TEXT)");
                await conn.ExecuteAsync("INSERT INTO _marker VALUES ('before')");
            }

            var info = await backup.BackupAsync(tenant);
            info.Name.ShouldEndWith(".db");
            info.Bytes.ShouldBeGreaterThan(0);

            // The manifest is real: sha256 recomputes over the snapshot.
            var backupPath = Path.Combine(fixture.DataDir, "backups", id, info.Name);
            File.Exists(backupPath + ".manifest.json").ShouldBeTrue();
            using (var stream = File.OpenRead(backupPath))
                Convert.ToHexString(await SHA256.HashDataAsync(stream))
                    .ToLowerInvariant().ShouldBe(info.Sha256);
            (await backup.ListAsync(id)).ShouldBe([info]);

            using (var conn = KeyedSqliteConnectionManager.OpenKeyed(tenant.DbPath, keyHex))
                await conn.ExecuteAsync("INSERT INTO _marker VALUES ('after')");

            await backup.RestoreAsync(tenant, info.Name);

            using (var conn = KeyedSqliteConnectionManager.OpenKeyed(tenant.DbPath, keyHex))
            {
                (await conn.QueryAsync<string>("SELECT v FROM _marker"))
                    .ShouldBe(["before"]);
                (await conn.ExecuteScalarAsync<string>("PRAGMA integrity_check"))
                    .ShouldBe("ok");
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }

    [Fact]
    public async Task Snapshot_and_restored_file_reject_wrong_or_absent_keys()
    {
        var (backup, svc, registry, _) = Sut();
        var a = fixture.NewTenant();
        var b = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(a, a, "A"));
            await svc.ProvisionAsync(new ProvisionRequest(b, b, "B"));
            var aTenant = (await registry.FindByIdAsync(a))!;
            var bKeyHex = await UnwrapAsync((await registry.FindByIdAsync(b))!);

            var info = await backup.BackupAsync(aTenant);
            await backup.RestoreAsync(aTenant, info.Name);
            var backupPath = Path.Combine(fixture.DataDir, "backups", a, info.Name);

            // The VACUUM INTO snapshot is SQLCipher-encrypted under A's
            // key — B's key and no key both read it as not-a-database,
            // and the restored live file is the same.
            AssertNotDatabase(backupPath, bKeyHex);
            AssertNotDatabase(backupPath, null);
            AssertNotDatabase(aTenant.DbPath, bKeyHex);
            AssertNotDatabase(aTenant.DbPath, null);
        }
        finally
        {
            var km = new TransitKeyManager(fixture.Transit);
            await km.DestroyAsync(a);
            await km.DestroyAsync(b);
        }
    }

    [Fact]
    public async Task Restore_rejects_tampered_manifest_and_bad_names()
    {
        var (backup, svc, registry, _) = Sut();
        var id = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
            var tenant = (await registry.FindByIdAsync(id))!;
            var info = await backup.BackupAsync(tenant);
            var dir = Path.Combine(fixture.DataDir, "backups", id);

            // A manifest whose hash no longer matches the snapshot is
            // refused before any bytes move.
            var manifestPath = Path.Combine(dir, info.Name + ".manifest.json");
            var tampered = info with { Sha256 = new string('0', 64) };
            await File.WriteAllTextAsync(manifestPath,
                JsonSerializer.Serialize(tampered));
            (await Should.ThrowAsync<InvalidOperationException>(
                () => backup.RestoreAsync(tenant, info.Name)))
                .Message.ShouldContain("sha256");

            await Should.ThrowAsync<FileNotFoundException>(
                () => backup.RestoreAsync(tenant, "never-made.db"));

            await Should.ThrowAsync<ArgumentException>(
                () => backup.RestoreAsync(tenant, $"..{Path.DirectorySeparatorChar}x.db"));
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }
}
