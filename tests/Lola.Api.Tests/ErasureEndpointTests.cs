using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-96 route proofs: DELETE is Provision-policy (operator) guarded,
/// 404s unknown tenants, and refuses already-retired tenants. The real
/// crypto-shred ordering is proven in Lola.DataPlane.Tests.
public sealed class ErasureEndpointTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Delete_requires_provision()
    {
        var client = factory.CreateClient();
        (await client.DeleteAsync("/api/admin/tenants/t1"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_erases_tenant_for_service()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(client));
        var tenant = $"ers{Guid.NewGuid():N}"[..12];
        (await client.PostAsJsonAsync("/api/admin/tenants",
                new { tenantId = tenant, slug = tenant, displayName = "E" }))
            .EnsureSuccessStatusCode();

        var resp = await client.DeleteAsync($"/api/admin/tenants/{tenant}");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("tenantId").GetString().ShouldBe(tenant);
    }

    [Fact]
    public async Task Delete_404s_unknown_and_400s_retired()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(client));

        (await client.DeleteAsync("/api/admin/tenants/no-such-tenant"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var retired = $"ret{Guid.NewGuid():N}"[..12];
        await factory.SeedUserAsync(retired, $"a@{retired}.dev",
            PkceLogin.Password, tenantStatus: "retired");
        (await client.DeleteAsync($"/api/admin/tenants/{retired}"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<string> ServiceTokenAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-svc",
                ["client_secret"] = "svc-secret-value",
                ["scope"] = "lola-api"
            }));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }
}
