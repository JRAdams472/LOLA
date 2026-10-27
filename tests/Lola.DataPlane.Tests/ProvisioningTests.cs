using Dapper;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Infrastructure.Crypto;
using Microsoft.Data.Sqlite;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// Marquee Phase-1 acceptance: provisioning produces an active registry
/// row, both transit keys, a SQLCipher-keyed file, and a `vault:`-wrapped
/// file key — all against real SQLite + real OpenBao.
[Collection("openbao")]
public sealed class ProvisioningTests(OpenBaoFixture fixture)
{
    static ProvisioningTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private (TenantProvisioningService svc, TenantRegistry registry) Sut()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"));
        db.Migrate();
        var registry = new TenantRegistry(db);
        var cipher = new TransitFieldCipher(fixture.Transit);
        return (new TenantProvisioningService(
                    registry,
                    new TransitKeyManager(fixture.Transit),
                    new TenantDbProvisioner(cipher),
                    new StubServiceClients(),
                    fixture.DataDir),
                registry);
    }



    [Fact]
    public async Task Provision_creates_working_isolated_tenant_end_to_end()
    {
        var (svc, registry) = Sut();
        var id = fixture.NewTenant();
        try
        {
            var result = await svc.ProvisionAsync(new ProvisionRequest(id, id, "Test Tenant"));
            result.Tenant.Status.ShouldBe("active");
            result.Tenant.DbKeyWrapped.ShouldStartWith("vault:v");
            result.StepsCompleted.ShouldBe(
                ["registry", "transit-keys", "data-file", "activate", "svc-client"]);

            // Transit keys exist (EnsureKeys ran); file exists and is
            // keyed: unkeyed open fails, keyed open works.
            File.Exists(result.Tenant.DbPath).ShouldBeTrue();
            AssertUnkeyedRefused(result.Tenant.DbPath);

            var wrapped = result.Tenant.DbKeyWrapped!;
            var keyB64 = await fixture.Transit.DecryptAsync($"tenant-{id}", wrapped);
            var keyHex = Convert.ToHexString(Convert.FromBase64String(keyB64));
            using var conn = KeyedOpen(result.Tenant.DbPath, keyHex);
            conn.ExecuteScalar<int>("SELECT version FROM schema_info").ShouldBe(1);
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }

    [Fact]
    public async Task Provision_is_reentrant_and_idempotent()
    {
        var (svc, _) = Sut();
        var id = fixture.NewTenant();
        try
        {
            var first = await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
            var second = await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));

            // Second run keeps the same wrapped key (no new file) and
            // only re-verifies the idempotent steps.
            second.Tenant.DbKeyWrapped.ShouldBe(first.Tenant.DbKeyWrapped);
            second.Tenant.Status.ShouldBe("active");
            second.StepsCompleted.ShouldNotContain("registry");
            second.StepsCompleted.ShouldNotContain("data-file");
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }

    [Fact]
    public async Task Provisioning_rejects_unsafe_ids()
    {
        var (svc, _) = Sut();
        await Should.ThrowAsync<ArgumentException>(
            () => svc.ProvisionAsync(new ProvisionRequest("../evil", "s", "D")));
        await Should.ThrowAsync<ArgumentException>(
            () => svc.ProvisionAsync(new ProvisionRequest("UPPER", "s", "D")));
        await Should.ThrowAsync<ArgumentException>(
            () => svc.ProvisionAsync(new ProvisionRequest("a/b", "s", "D")));
    }

    [Fact]
    public async Task Tenant_files_are_crypto_isolated()
    {
        var (svc, registry) = Sut();
        var a = fixture.NewTenant();
        var b = fixture.NewTenant();
        try
        {
            var ta = (await svc.ProvisionAsync(new ProvisionRequest(a, a, "A"))).Tenant;
            var tb = (await svc.ProvisionAsync(new ProvisionRequest(b, b, "B"))).Tenant;

            // Unwrap each tenant's file key via its own transit key…
            var keyA = Convert.ToHexString(Convert.FromBase64String(
                await fixture.Transit.DecryptAsync($"tenant-{a}", ta.DbKeyWrapped!)));
            var keyB = Convert.ToHexString(Convert.FromBase64String(
                await fixture.Transit.DecryptAsync($"tenant-{b}", tb.DbKeyWrapped!)));

            keyA.ShouldNotBe(keyB); // distinct file keys per tenant

            // …then try B's key against A's file — SQLCipher must refuse.
            Should.Throw<SqliteException>(() =>
            {
                using var wrong = KeyedOpen(ta.DbPath, keyB);
                wrong.ExecuteScalar<int>("SELECT version FROM schema_info");
            });
        }
        finally
        {
            var km = new TransitKeyManager(fixture.Transit);
            await km.DestroyAsync(a);
            await km.DestroyAsync(b);
        }
    }

    private static SqliteConnection KeyedOpen(string path, string keyHex)
    {
        var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        conn.Execute($"""
            PRAGMA key = "x'{keyHex}'";
            """);
        return conn;
    }

    private static void AssertUnkeyedRefused(string path)
    {
        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        Should.Throw<SqliteException>(
            () => conn.ExecuteScalar("SELECT count(*) FROM sqlite_master"));
    }
}
