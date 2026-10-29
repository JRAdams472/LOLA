using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-91/129: the global tenant-registry surface belongs to the
/// platform operator — `lola-ops` and Service reach it; a TenantAdmin
/// principal gets 403. Tenant-scoped admin routes are unchanged.
public sealed class PlatformOpsTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Global_tenant_surface_requires_ops_or_service()
    {
        // Service client (automation) still reaches the surface.
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await TokenAsync(svc, "lola-svc", "svc-secret-value"));
        (await svc.GetAsync("/api/admin/tenants"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // The platform operator owns it outright.
        var ops = factory.CreateClient();
        ops.DefaultRequestHeaders.Authorization =
            new("Bearer", await TokenAsync(ops, "lola-ops", "ops-secret-value"));
        (await ops.GetAsync("/api/admin/tenants"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var tenant = $"ops{Guid.NewGuid():N}"[..12];
        (await ops.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Ops Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task TenantAdmin_is_denied_the_global_surface()
    {
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await TokenAsync(svc, "lola-svc", "svc-secret-value"));
        var tenant = $"ta{Guid.NewGuid():N}"[..12];
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "T" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        // A real TenantAdmin user: tenant-scoped routes fine, global 403.
        await factory.SeedUserAsync(tenant,
            $"adm-{tenant}@test.dev", PkceLogin.Password, LolaRole.TenantAdmin);
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.TenantAdmin,
                email: $"adm-{tenant}@test.dev", seed: false));

        (await admin.GetAsync("/api/admin/tenants"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // ...while the tenant-scoped admin surface stays open to them.
        (await admin.GetAsync($"/api/admin/tenants/{tenant}/users"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<string> TokenAsync(
        HttpClient client, string id, string secret)
    {
        var resp = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = id,
                ["client_secret"] = secret,
                ["scope"] = "lola-api"
            }));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }
}
