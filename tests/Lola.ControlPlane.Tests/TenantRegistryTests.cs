using Lola.ControlPlane;
using Shouldly;

namespace Lola.ControlPlane.Tests;

public class TenantRegistryTests
{
    private static TenantRegistry Sut()
    {
        var db = new ControlPlaneDb(
            Path.Combine(Path.GetTempPath(), "lola-tests", $"reg-{Guid.NewGuid():N}.db"));
        db.Migrate();
        return new TenantRegistry(db);
    }

    [Fact]
    public async Task Provisioning_insert_then_find_and_activate()
    {
        var registry = Sut();
        await registry.InsertProvisioningAsync("acme", "acme", "Acme Lending", "data/tenants/acme.db");

        var tenant = await registry.FindByIdAsync("acme");
        tenant.ShouldNotBeNull();
        tenant.Status.ShouldBe("provisioning");
        tenant.DbKeyWrapped.ShouldBeNull();
        tenant.DbPath.ShouldBe("data/tenants/acme.db");

        await registry.SetDbKeyAsync("acme", "vault:v1:wrappedkey");
        await registry.SetStatusAsync("acme", "active");

        tenant = await registry.FindByIdAsync("acme");
        tenant!.Status.ShouldBe("active");
        tenant.DbKeyWrapped.ShouldBe("vault:v1:wrappedkey");
    }

    [Fact]
    public async Task Insert_is_idempotent_for_retries()
    {
        var registry = Sut();
        await registry.InsertProvisioningAsync("acme", "acme", "Acme", "data/tenants/acme.db");
        await registry.SetStatusAsync("acme", "active");

        // Re-run (provisioning retry) — existing row wins, no UNIQUE failure.
        await registry.InsertProvisioningAsync("acme", "acme", "Acme", "data/tenants/acme.db");

        var tenant = await registry.FindByIdAsync("acme");
        tenant!.Status.ShouldBe("active");
        (await registry.ListAsync()).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Slug_lookup_and_list()
    {
        var registry = Sut();
        await registry.InsertProvisioningAsync("t-one", "one", "One", "data/tenants/t-one.db");
        await registry.InsertProvisioningAsync("t-two", "two", "Two", "data/tenants/t-two.db");

        (await registry.FindBySlugAsync("two"))!.Id.ShouldBe("t-two");
        (await registry.ListAsync()).Select(t => t.Id).ShouldBe(["t-one", "t-two"], ignoreOrder: true);
        (await registry.FindBySlugAsync("nope")).ShouldBeNull();
    }
}
