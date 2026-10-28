using Dapper;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Infrastructure.Crypto;
using Shouldly;

namespace Lola.DataPlane.Tests;

/// The factory opens connections ONLY for the TenantContext tenant —
/// it has no tenant-id parameter, so no code path can name another
/// tenant's file.
[Collection("openbao")]
public sealed class TenantConnectionFactoryTests(OpenBaoFixture fixture)
{
    [Fact]
    public async Task Opens_keyed_connection_for_scoped_tenant()
    {
        var id = fixture.NewTenant();
        try
        {
            var db = new ControlPlaneDb(Path.Combine(fixture.DataDir, $"c-{Guid.NewGuid():N}.db"));
            db.Migrate();
            var registry = new TenantRegistry(db);
            var cipher = new TransitFieldCipher(fixture.Transit);
            var svc = new TenantProvisioningService(
                registry, new TransitKeyManager(fixture.Transit),
                new TenantDbProvisioner(cipher), new StubServiceClients(),
                new TenantMigrationRunner(cipher, registry, new TenantMigrationTracker(db)),
                fixture.DataDir);
            var tenant = (await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"))).Tenant;

            var factory = new TenantConnectionFactory(cipher);
            using (TenantContext.Use(TenantScope.From(tenant)))
            using (var conn = await factory.OpenAsync())
            {
                conn.ExecuteScalar<int>("SELECT version FROM schema_info").ShouldBe(1);
                conn.ExecuteScalar<long>("PRAGMA foreign_keys").ShouldBe(1);
                conn.ExecuteScalar<long>("PRAGMA secure_delete").ShouldBeGreaterThanOrEqualTo(1);
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }

    [Fact]
    public async Task Missing_tenant_scope_throws()
    {
        var factory = new TenantConnectionFactory(
            new TransitFieldCipher(fixture.Transit));
        await Should.ThrowAsync<InvalidOperationException>(() => factory.OpenAsync());
    }
}
