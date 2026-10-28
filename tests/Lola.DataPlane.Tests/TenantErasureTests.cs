using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-107 acceptance: a retried or partially-failed erasure converges
/// to `retired` — already-destroyed transit keys read as absent, and
/// file cleanup is best-effort (locked files are reported, not fatal).
/// Real OpenBao + real SQLCipher.
[Collection("openbao")]
public sealed class TenantErasureTests(OpenBaoFixture fixture)
{
    static TenantErasureTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private (TenantErasureService eraser, TenantProvisioningService svc,
             TenantRegistry registry, TransitKeyManager keys) Sut()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"));
        db.Migrate();
        var registry = new TenantRegistry(db);
        var cipher = new TransitFieldCipher(fixture.Transit);
        var keys = new TransitKeyManager(fixture.Transit);
        var runner = new TenantMigrationRunner(
            cipher, registry, new TenantMigrationTracker(db));
        var svc = new TenantProvisioningService(
            registry, keys, new TenantDbProvisioner(cipher),
            new StubServiceClients(), runner, fixture.DataDir);
        var eraser = new TenantErasureService(
            keys, registry, Path.Combine(fixture.DataDir, "backups"));
        return (eraser, svc, registry, keys);
    }

    [Fact]
    public async Task Erase_is_retry_safe_after_partial_failure()
    {
        var (eraser, svc, registry, keys) = Sut();
        var id = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
            var tenant = (await registry.FindByIdAsync(id))!;

            // Simulate a previous erase that died AFTER destroying keys
            // but before deleting files / marking retired — the wedge
            // state MPE-107 fixes: keys gone, files live, status active.
            await keys.DestroyAsync(id);
            File.Exists(tenant.DbPath).ShouldBeTrue();

            // The retry must converge, not die on the missing keys.
            var result = await eraser.EraseAsync(tenant);

            result.FilesRemoved.ShouldBeGreaterThan(0);
            (await registry.FindByIdAsync(id))!.Status.ShouldBe("retired");
            File.Exists(tenant.DbPath).ShouldBeFalse();
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }

    [Fact]
    public async Task Erase_converges_to_retired_when_cleanup_fails()
    {
        var (eraser, svc, registry, _) = Sut();
        var id = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
            var tenant = (await registry.FindByIdAsync(id))!;

            // Hold the tenant file open — File.Delete fails on Windows
            // (no-op unlink on Linux). Either way the lifecycle must
            // converge: once keys are dead the bytes are unreadable, so
            // `retired` still lands and leftovers are reported.
            await using (var lock_ = new FileStream(
                tenant.DbPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var result = await eraser.EraseAsync(tenant);

                (await registry.FindByIdAsync(id))!.Status.ShouldBe("retired");
                if (File.Exists(tenant.DbPath))
                    result.UnresolvedPaths.ShouldContain(tenant.DbPath);
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }
}
