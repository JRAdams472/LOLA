using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-95 route proofs: the backup/restore surface is Provision-policy
/// (service operator) guarded, resolves the tenant, and names only
/// manifests that exist. Backup mechanics live in DataPlane tests.
public sealed class BackupEndpointTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Backup_routes_require_provision()
    {
        var client = factory.CreateClient();
        (await client.PostAsync("/api/admin/tenants/t1/backup", null))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/admin/tenants/t1/backups"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync("/api/admin/tenants/t1/restore",
                new { name = "x.db" }))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Backup_list_restore_flow_for_service()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(client));
        var tenant = $"bkt{Guid.NewGuid():N}"[..12];
        (await client.PostAsJsonAsync("/api/admin/tenants",
                new { tenantId = tenant, slug = tenant, displayName = "BK" }))
            .EnsureSuccessStatusCode();

        var backup = await client.PostAsync(
            $"/api/admin/tenants/{tenant}/backup", null);
        backup.StatusCode.ShouldBe(HttpStatusCode.OK);
        var name = (await backup.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("name").GetString()!;

        var list = await client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/tenants/{tenant}/backups");
        list.GetArrayLength().ShouldBe(1);
        list[0].GetProperty("name").GetString().ShouldBe(name);

        (await client.PostAsJsonAsync($"/api/admin/tenants/{tenant}/restore",
                new { name }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Backup_404s_on_unknown_tenant_and_unknown_snapshot()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(client));

        (await client.PostAsync("/api/admin/tenants/no-such-tenant/backup", null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/admin/tenants/no-such-tenant/backups"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var tenant = $"bkn{Guid.NewGuid():N}"[..12];
        (await client.PostAsJsonAsync("/api/admin/tenants",
                new { tenantId = tenant, slug = tenant, displayName = "BK" }))
            .EnsureSuccessStatusCode();
        // The fake lists one snapshot — a name that isn't it is 404,
        // which is also the traversal guard's path.
        (await client.PostAsJsonAsync($"/api/admin/tenants/{tenant}/restore",
                new { name = "../control.db" }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
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
