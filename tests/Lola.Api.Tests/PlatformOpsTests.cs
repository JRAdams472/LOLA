using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Dapper;
using Lola.Api.Identity;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-91/129/271: the global tenant-registry surface belongs to
/// designated platform clients — `lola-ops` and `lola-svc` mint the
/// dedicated authority claim. Tenant-bound principals (svc-{tenant},
/// TenantAdmin, any human — even one carrying a legacy Service group
/// grant) are denied before a lifecycle handler can run.
public sealed class PlatformOpsTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    /// Every PlatformOps route in AdminEndpoints — the fleet inventory
    /// (tenant registry, migrations, backup lifecycle, erasure, ISPPr
    /// republish). Bodies stay well-formed so a denial is the policy,
    /// never a model-validation failure.
    private static IEnumerable<(HttpMethod Method, string Path, object? Body)> FleetRoutes(
        string tenantId, string ghostId)
    {
        yield return (HttpMethod.Get, "/api/admin/tenants", null);
        yield return (HttpMethod.Post, "/api/admin/tenants",
            new { tenantId = ghostId, slug = ghostId, displayName = "Ghost Co" });
        yield return (HttpMethod.Post, "/api/admin/migrations", null);
        yield return (HttpMethod.Post, $"/api/admin/tenants/{tenantId}/backup", null);
        yield return (HttpMethod.Get, $"/api/admin/tenants/{tenantId}/backups", null);
        yield return (HttpMethod.Post, $"/api/admin/tenants/{tenantId}/restore",
            new { name = "never.db" });
        yield return (HttpMethod.Delete, $"/api/admin/tenants/{tenantId}", null);
        yield return (HttpMethod.Post,
            $"/api/admin/tenants/{tenantId}/isppr/republish-templates", null);
    }

    [Fact]
    public async Task Global_tenant_surface_requires_ops_or_service()
    {
        // Designated global automation still reaches the surface.
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await TokenAsync(svc, "lola-svc", "svc-secret-value"));
        (await svc.GetAsync("/api/admin/tenants"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        // ...across the non-destructive fleet routes, not just the list.
        (await svc.PostAsync("/api/admin/migrations", null))
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
        (await ops.GetAsync($"/api/admin/tenants/{tenant}/backups"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
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

    [Fact]
    public async Task Tenant_service_principal_is_denied_every_fleet_route()
    {
        var (tenantA, tenantB) = ($"a{Guid.NewGuid():N}"[..12], $"b{Guid.NewGuid():N}"[..12]);
        var ghost = $"g{Guid.NewGuid():N}"[..12];
        var ops = factory.CreateClient();
        ops.DefaultRequestHeaders.Authorization =
            new("Bearer", await TokenAsync(ops, "lola-ops", "ops-secret-value"));
        foreach (var t in new[] { tenantA, tenantB })
            (await ops.PostAsJsonAsync("/api/admin/tenants",
                new { tenantId = t, slug = t, displayName = "Co" }))
                .StatusCode.ShouldBe(HttpStatusCode.Created);

        // svc-A: tenant-bound Service credentials — pre-MPE-271 this
        // token reached every route below through the bare Service role.
        await factory.ResetServiceClientSecretAsync(tenantA, "svc-secret-value");
        var svcA = factory.CreateClient();
        svcA.DefaultRequestHeaders.Authorization = new("Bearer",
            await TokenAsync(svcA, $"svc-{tenantA}", "svc-secret-value"));

        foreach (var (method, path, body) in FleetRoutes(tenantA, ghost))
            (await SendAsync(svcA, method, path, body))
                .ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");

        // Zero handler side effects: A was not erased and its status
        // still resolves; the denied create left nothing behind.
        (await svcA.GetAsync($"/api/admin/tenants/{tenantA}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var tenants = await (await ops.GetAsync("/api/admin/tenants"))
            .Content.ReadFromJsonAsync<JsonElement>();
        tenants.EnumerateArray().Select(t => t.GetProperty("id").GetString())
            .ShouldNotContain(ghost);
        // ...and its tenant scope is still honest: own status yes, B no.
        (await svcA.GetAsync($"/api/admin/tenants/{tenantB}"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Human_principals_and_anonymous_are_denied_every_fleet_route()
    {
        var tenant = $"h{Guid.NewGuid():N}"[..12];
        var ghost = $"g{Guid.NewGuid():N}"[..12];
        var ops = factory.CreateClient();
        ops.DefaultRequestHeaders.Authorization =
            new("Bearer", await TokenAsync(ops, "lola-ops", "ops-secret-value"));
        (await ops.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        foreach (var role in new[] { LolaRole.TenantAdmin, LolaRole.LoanOfficer, LolaRole.Borrower })
        {
            var human = factory.CreateClient();
            human.DefaultRequestHeaders.Authorization = new("Bearer",
                await PkceLogin.TokenAsync(factory, tenant, role));
            foreach (var (method, path, body) in FleetRoutes(tenant, ghost))
                (await SendAsync(human, method, path, body))
                    .ShouldBe(HttpStatusCode.Forbidden, $"{role}: {method} {path}");
        }

        var anon = factory.CreateClient();
        foreach (var (method, path, body) in FleetRoutes(tenant, ghost))
            (await SendAsync(anon, method, path, body))
                .ShouldBe(HttpStatusCode.Unauthorized, $"anonymous: {method} {path}");
    }

    [Fact]
    public async Task Legacy_service_group_grant_cannot_reach_fleet()
    {
        // group_roles predates the assignable-role restriction — a
        // 'Service' row can exist. Members mint a Service role claim
        // but never the platform claim, so fleet stays denied while
        // the tenant-scoped Service surfaces still work (exactly the
        // surface the startup reconciliation report flags).
        var tenant = $"lg{Guid.NewGuid():N}"[..11];
        var user = await factory.SeedUserAsync(tenant,
            $"svc-{tenant}@test.dev", PkceLogin.Password, LolaRole.Borrower);
        var db = factory.Services.GetRequiredService<ControlPlaneDb>();
        using (var conn = db.Open())
        {
            var gid = $"g-{Guid.NewGuid():N}";
            await conn.ExecuteAsync(
                "INSERT INTO groups (id, tenant_id, name, name_idx) " +
                "VALUES (@gid, @t, @n, @ni)",
                new { gid, t = tenant, n = TestCrypto.Enc(gid), ni = TestCrypto.Idx(gid) });
            await conn.ExecuteAsync(
                "INSERT INTO group_roles (group_id, role) VALUES (@gid, 'Service')",
                new { gid });
            await conn.ExecuteAsync(
                "INSERT INTO user_groups (user_id, group_id) VALUES (@uid, @gid)",
                new { uid = user.Id, gid });
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower,
                email: $"svc-{tenant}@test.dev", seed: false));

        foreach (var (method, path, body) in FleetRoutes(tenant, $"g{Guid.NewGuid():N}"[..12]))
            (await SendAsync(client, method, path, body))
                .ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
        // The Service grant itself is untouched: own-tenant status
        // (AdminRead + GuardedStatus) remains readable for the member.
        (await client.GetAsync($"/api/admin/tenants/{tenant}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Group_mutations_cannot_assign_machine_authority()
    {
        var tenant = $"gm{Guid.NewGuid():N}"[..11];
        var ops = factory.CreateClient();
        ops.DefaultRequestHeaders.Authorization =
            new("Bearer", await TokenAsync(ops, "lola-ops", "ops-secret-value"));
        (await ops.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.TenantAdmin));

        // Create-time: machine and unknown/numeric roles all rejected.
        foreach (var role in new[] { "Service", "lola-ops", "99", "Platform" })
            (await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant}/groups",
                new { name = $"x-{role}", description = "d", roles = new[] { role } }))
                .StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"roles [{role}]");

        // Update-time: a legit group cannot be pivoted to Service, and
        // its roles survive the rejected edit untouched.
        var made = await admin.PostAsJsonAsync($"/api/admin/tenants/{tenant}/groups",
            new { name = "officers", description = "d", roles = new[] { "LoanOfficer" } });
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        var gid = (await made.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        (await admin.PutAsJsonAsync($"/api/admin/tenants/{tenant}/groups/{gid}",
            new
            {
                name = (string?)null,
                description = (string?)null,
                roles = new[] { "Service" }
            }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var groups = await (await admin.GetAsync($"/api/admin/tenants/{tenant}/groups"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var group = groups.EnumerateArray().Single(g => g.GetProperty("id").GetString() == gid);
        group.GetProperty("roles").EnumerateArray()
            .Select(r => r.GetString()).ShouldBe(["LoanOfficer"]);
    }

    [Theory]
    // authority claim, tid claim, expected
    [InlineData(true, false, true)]    // designated global client
    [InlineData(true, true, false)]    // conflicting grant — fail closed
    [InlineData(false, false, false)]  // tenant/human principal
    [InlineData(false, true, false)]   // tenant-bound service
    public void IsPlatformPrincipal_requires_authority_and_no_tenant_binding(
        bool authority, bool tid, bool expected)
    {
        var claims = new List<Claim>();
        if (authority) claims.Add(new(PlatformAuthority.Claim, PlatformAuthority.Value));
        if (tid) claims.Add(new("tid", "t-1"));
        claims.Add(new(ClaimTypes.Role, "Service"));
        PlatformAuthority.IsPlatformPrincipal(new ClaimsPrincipal(
            new ClaimsIdentity(claims, "test"))).ShouldBe(expected);

        // The old shortcuts never qualify on their own.
        PlatformAuthority.IsPlatformPrincipal(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Role, "lola-ops")], "test")))
            .ShouldBeFalse();
    }

    private static async Task<HttpStatusCode> SendAsync(
        HttpClient client, HttpMethod method, string path, object? body)
    {
        var resp = method == HttpMethod.Get ? await client.GetAsync(path)
            : method == HttpMethod.Delete ? await client.DeleteAsync(path)
            : await client.PostAsJsonAsync(path, body ?? new { });
        return resp.StatusCode;
    }

    private static async Task<string> TokenAsync(
        HttpClient client, string id, string secret)
    {
        var resp = await PkceLogin.PostWithRetryAsync(client, "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = id,
                ["client_secret"] = secret,
                ["scope"] = "lola-api"
            });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }
}
