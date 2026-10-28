using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-87 acceptance: the marquee flow — provision a tenant → officer
/// PKCE login → invite a borrower → accept → borrower PKCE login →
/// `tid` claim → tenant-scoped access only. Plus invite single-use/
/// expiry, svc-client tid scoping, and the auth-tight rate limit.
public sealed class ProvisioningAndInviteTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{

    [Fact]
    public async Task Marquee_provision_invite_accept_borrower_login()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc, "lola-svc", "svc-secret-value"));

        // 1. Provision — service client + secret returned exactly once.
        var made = await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Marquee Co" });
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        var prov = await made.Content.ReadFromJsonAsync<JsonElement>();
        prov.GetProperty("tenant").GetProperty("status").GetString().ShouldBe("active");
        prov.GetProperty("serviceClientId").GetString().ShouldBe($"svc-{tenant}");
        var svcSecret = prov.GetProperty("serviceClientSecret").GetString();
        svcSecret.ShouldNotBeNullOrEmpty();

        // Idempotent re-run — no secret a second time.
        var again = await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Marquee Co" });
        (await again.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("serviceClientSecret").GetString().ShouldBeNull();

        // Status view — service sees the provisioned row.
        (await svc.GetAsync($"/api/admin/tenants/{tenant}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // 2. Officer user + PKCE login → create the borrower invite.
        var officerToken = await PkceLogin.TokenAsync(factory, tenant, LolaRole.TenantAdmin);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer", officerToken);

        var invite = await officer.PostAsJsonAsync(
            $"/api/admin/tenants/{tenant}/invites",
            new { email = $"borrower-{tenant}@test.dev" });
        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
        var link = (await invite.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("inviteUrl").GetString()!;
        var token = Uri.UnescapeDataString(link.Split("token=")[1]);

        // 3. Accept — creates the borrower user; single-use enforced.
        var accept = await factory.CreateClient().PostAsJsonAsync(
            "/api/invites/accept",
            new { token, displayName = "Borrower One", password = PkceLogin.Password });
        accept.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.CreateClient().PostAsJsonAsync("/api/invites/accept",
                new { token, displayName = "Again", password = PkceLogin.Password }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // 4. Borrower PKCE login — token carries tid + Borrower role.
        var borrowerToken = await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower,
            "lola-borrower-portal", "http://localhost:3300/callback",
            email: $"borrower-{tenant}@test.dev", seed: false);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer", borrowerToken);
        var info = await borrower.GetFromJsonAsync<JsonElement>("/connect/userinfo");
        info.GetProperty("tid").GetString().ShouldBe(tenant);
        info.GetProperty("roles")[0].GetString().ShouldBe("Borrower");

        // 5. Tenant-scoped access only — admin surface refuses Borrower.
        (await borrower.GetAsync($"/api/admin/tenants/{tenant}/users"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // 6. The provisioned svc client — client-credentials works and
        // its token is scoped to its own tenant.
        var svcT = factory.CreateClient();
        svcT.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svcT, $"svc-{tenant}", svcSecret!));
        (await svcT.GetAsync($"/api/admin/tenants/{tenant}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await svcT.GetAsync("/api/admin/tenants/other-tenant"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Invite_expired_and_unknown_tokens_are_rejected()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc, "lola-svc", "svc-secret-value"));
        await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Expiry Co" });

        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/invites/accept",
                new { token = "bogus-token", displayName = "X", password = PkceLogin.Password }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Expired invite — seed a past-TTL row directly, then accept.
        var expiredToken = "expired-invite-token";
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.ASCII.GetBytes(expiredToken))).ToLowerInvariant();
        var db = factory.Services.GetRequiredService<ControlPlaneDb>();
        using (var conn = db.Open())
        {
            await conn.ExecuteAsync("""
                INSERT INTO invites (id, tenant_id, email, role, token_hash,
                                     expires_utc, created_by)
                VALUES (@id, @t, @e, 'Borrower', @h, @exp, 'test')
                """, new
            {
                id = $"i-{Guid.NewGuid():N}",
                t = tenant,
                e = $"expired-{tenant}@test.dev",
                h = hash,
                exp = DateTimeOffset.UtcNow.AddHours(-1).ToString("O")
            });
        }
        var resp = await client.PostAsJsonAsync("/api/invites/accept",
            new { token = expiredToken, displayName = "Late", password = PkceLogin.Password });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("expired");
    }

    [Fact]
    public async Task Migrations_endpoint_runs_rollout_for_service()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(client, "lola-svc", "svc-secret-value"));
        var resp = await client.PostAsync("/api/admin/migrations", null);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("applied").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Auth_tight_rate_limit_kicks_in_on_token_endpoint()
    {
        // The auth-tight partition keys on RemoteIpAddress — null under
        // TestServer, so every request shares the class-host "unknown"
        // bucket. A dedicated factory gets its own host + fresh bucket
        // and can't starve (or be starved by) the marquee flow.
        using var isolated = new LolaApiFactory();
        var client = isolated.CreateClient();
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "lola-svc",
            ["client_secret"] = "wrong",
            ["scope"] = "lola-api"
        });
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
            statuses.Add((await client.PostAsync("/connect/token", body)).StatusCode);
        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }

    private static async Task<string> ServiceTokenAsync(
        HttpClient client, string clientId, string secret)
    {
        var resp = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["scope"] = "lola-api"
            }));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }
}
