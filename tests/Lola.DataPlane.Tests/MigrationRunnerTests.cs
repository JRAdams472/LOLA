using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-93 acceptance: migrations roll across N tenant files with
/// per-tenant status tracking; failures isolate per tenant.
[Collection("openbao")]
public sealed class MigrationRunnerTests(OpenBaoFixture fixture)
{
    static MigrationRunnerTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private (TenantMigrationRunner runner, TenantProvisioningService svc,
             TenantRegistry registry, TenantMigrationTracker tracker) Sut()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"));
        db.Migrate();
        var registry = new TenantRegistry(db);
        var tracker = new TenantMigrationTracker(db);
        var cipher = new TransitFieldCipher(fixture.Transit);
        var runner = new TenantMigrationRunner(cipher, registry, tracker);
        var svc = new TenantProvisioningService(
            registry, new TransitKeyManager(fixture.Transit),
            new TenantDbProvisioner(cipher), new StubServiceClients(),
            runner, fixture.DataDir);
        return (runner, svc, registry, tracker);
    }

    [Fact]
    public async Task Provision_migrates_and_rolls_the_fleet()
    {
        var (runner, svc, registry, tracker) = Sut();
        var a = fixture.NewTenant();
        var b = fixture.NewTenant();
        var c = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(a, a, "A"));
            await svc.ProvisionAsync(new ProvisionRequest(b, b, "B"));
            // C is pre-provisioned WITHOUT migrate (simulates an older
            // file) — the rollout must bring it to latest.
            await registry.InsertProvisioningAsync(c, c, "C",
                Path.Combine(fixture.DataDir, $"{c}.db"));
            var km = new TransitKeyManager(fixture.Transit);
            await km.EnsureKeysAsync(c);
            var cipher = new TransitFieldCipher(fixture.Transit);
            var cTenant = (await registry.FindByIdAsync(c))!;
            var wrapped = await new TenantDbProvisioner(cipher).ProvisionFileAsync(cTenant);
            await registry.SetDbKeyAsync(c, wrapped);
            await registry.SetStatusAsync(c, "active");

            var results = await runner.MigrateAllAsync();
            results.Count.ShouldBe(3);
            results.ShouldAllBe(r => r.Success);
            var rows = await tracker.ListAsync();
            rows.Select(r => r.TenantId).ShouldContain(a);
            rows.Select(r => r.TenantId).ShouldContain(c);
            rows.Where(r => r.TenantId == c).ShouldAllBe(r => r.Status == "applied");
        }
        finally
        {
            var km = new TransitKeyManager(fixture.Transit);
            await km.DestroyAsync(a); await km.DestroyAsync(b); await km.DestroyAsync(c);
        }
    }

    [Fact]
    public async Task Failure_is_isolated_per_tenant()
    {
        var (runner, svc, registry, tracker) = Sut();
        var good = fixture.NewTenant();
        var bad = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(good, good, "G"));
            // `bad` has a registry row + garbage wrapped key — unwrap
            // fails; the fleet rollout must record it and carry on.
            await registry.InsertProvisioningAsync(bad, bad, "B",
                Path.Combine(fixture.DataDir, $"{bad}.db"));
            await registry.SetDbKeyAsync(bad, "vault:v1:not-a-real-key");
            await registry.SetStatusAsync(bad, "active");

            var results = await runner.MigrateAllAsync();
            results.Single(r => r.TenantId == good).Success.ShouldBeTrue();
            var badResult = results.Single(r => r.TenantId == bad);
            badResult.Success.ShouldBeFalse();
            var badRow = (await tracker.ListAsync(bad)).Single();
            badRow.Status.ShouldBe("failed");
            badRow.Error.ShouldNotBeNullOrEmpty();
        }
        finally
        {
            var km = new TransitKeyManager(fixture.Transit);
            await km.DestroyAsync(good);
            // `bad` never had transit keys created — nothing to destroy.
        }
    }

    [Fact]
    public async Task Remigration_is_a_noop()
    {
        var (runner, svc, registry, _) = Sut();
        var id = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
            var tenant = (await registry.FindByIdAsync(id))!;
            var second = await runner.MigrateAsync(tenant);
            second.Success.ShouldBeTrue();
            second.Scripts.ShouldBeEmpty(); // nothing left to apply
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }
}
