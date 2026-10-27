using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-86 acceptance: tenant-resolution middleware (403 on
/// unknown/inactive/mismatched tenants), role policies, and the
/// role→visibility masking map enforced identically on REST + GraphQL.
public sealed class TenantAccessTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private const string Password = "correct horse battery staple";

    [Theory]
    [InlineData(LolaRole.TenantAdmin, false, false)] // full plaintext
    [InlineData(LolaRole.LoanOfficer, false, true)]  // Contact/Name plaintext, Account masked
    [InlineData(LolaRole.Service, true, true)]       // all masked
    public async Task Users_endpoint_masks_per_role_visibility_map(
        LolaRole role, bool expectMaskedContact, bool expectMaskedAccount)
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var token = await LoginAsync(tenant, role);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var users = await client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/tenants/{tenant}/users");

        var me = users.EnumerateArray().First(u =>
            u.GetProperty("email").GetString()!.EndsWith("@test.dev") ||
            u.GetProperty("email").GetString()!.StartsWith('•'));

        if (expectMaskedContact)
        {
            me.GetProperty("email").GetString().ShouldStartWith("•••");
            me.GetProperty("displayName").GetString().ShouldStartWith("•••");
        }
        else
        {
            me.GetProperty("email").GetString()!.ShouldContain("@test.dev");
            me.GetProperty("displayName").GetString().ShouldBe("Test User");
        }

        var mfaRef = me.GetProperty("mfaSecretRef").GetString();
        if (expectMaskedAccount)
        {
            mfaRef.ShouldStartWith("•••");
            mfaRef.ShouldEndWith("1234"); // last-4 survives the mask
        }
        else
        {
            mfaRef.ShouldBe("vault:dev-mfa-ref-1234");
        }
    }

    [Fact]
    public async Task Graphql_tenantUsers_masks_identically_to_rest()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var token = await LoginAsync(tenant, LolaRole.Service);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var rest = await client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/tenants/{tenant}/users");
        var gql = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""{ tenantUsers(tenantId: "{{tenant}}") { email displayName status roles mfaSecretRef } }"""
        });
        gql.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await gql.Content.ReadFromJsonAsync<JsonElement>();
        var gqlUsers = body.GetProperty("data").GetProperty("tenantUsers");

        var restMasked = rest.EnumerateArray().Select(u =>
            u.GetProperty("email").GetString()).ToList();
        var gqlMasked = gqlUsers.EnumerateArray().Select(u =>
            u.GetProperty("email").GetString()).ToList();
        gqlMasked.ShouldBe(restMasked);
        gqlUsers.EnumerateArray().All(u =>
            u.GetProperty("email").GetString()!.StartsWith('•')).ShouldBeTrue();
    }

    [Fact]
    public async Task Cross_tenant_path_is_403_and_isolation_holds()
    {
        var a = $"t{Guid.NewGuid():N}"[..12];
        var b = $"t{Guid.NewGuid():N}"[..12];
        await factory.SeedUserAsync(b, $"{b}@test.dev", Password);
        var tokenA = await LoginAsync(a, LolaRole.TenantAdmin);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", tokenA);

        // A's token cannot name B's tenant in any admin path.
        (await client.GetAsync($"/api/admin/tenants/{b}/users"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/admin/tenants/{b}/groups"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // A's own list contains only A users — no B leakage.
        var users = await client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/tenants/{a}/users");
        users.EnumerateArray().All(u =>
            u.GetProperty("email").GetString()!.Contains(a)).ShouldBeTrue();
    }

    [Fact]
    public async Task Inactive_tenant_token_is_403()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        // Provisioning-status tenant — resolution must refuse.
        var token = await LoginAsync(tenant, LolaRole.TenantAdmin, tenantStatus: "provisioning");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var resp = await client.GetAsync($"/api/admin/tenants/{tenant}/users");
        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).ShouldContain("tenant");
    }

    [Fact]
    public async Task Borrower_is_denied_admin_endpoints()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var token = await LoginAsync(tenant, LolaRole.Borrower);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        (await client.GetAsync($"/api/admin/tenants/{tenant}/users"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/admin/tenants"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Traversal_and_garbage_tenant_ids_are_403()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var token = await LoginAsync(tenant, LolaRole.TenantAdmin);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // Crafted path segments can never resolve — no file is named.
        (await client.GetAsync("/api/admin/tenants/..%2F..%2Fetc/groups"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/admin/tenants/nonexistent-tenant/users"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Group_crud_membership_and_officer_user_round_trip()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var token = await LoginAsync(tenant, LolaRole.TenantAdmin);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // Create a group with the LoanOfficer role.
        var created = await client.PostAsJsonAsync(
            $"/api/admin/tenants/{tenant}/groups",
            new { name = "officers", description = "d", roles = new[] { "LoanOfficer" } });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var group = await created.Content.ReadFromJsonAsync<JsonElement>();
        var gid = group.GetProperty("id").GetString()!;
        group.GetProperty("roles")[0].GetString().ShouldBe("LoanOfficer");

        // Create an officer user.
        var made = await client.PostAsJsonAsync(
            $"/api/admin/tenants/{tenant}/users",
            new
            {
                email = $"officer-{tenant}@test.dev",
                displayName = "Officer One",
                password = Password
            });
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = await made.Content.ReadFromJsonAsync<JsonElement>();
        var uid = officer.GetProperty("id").GetString()!;
        officer.GetProperty("status").GetString().ShouldBe("active");

        // Assign to group — member count + effective role appear.
        var assign = await client.PostAsJsonAsync(
            $"/api/admin/tenants/{tenant}/groups/{gid}/members",
            new { userId = uid });
        assign.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await assign.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("memberCount").GetInt32().ShouldBe(1);

        var users = await client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/tenants/{tenant}/users");
        var officerRow = users.EnumerateArray().First(u =>
            u.GetProperty("id").GetString() == uid);
        officerRow.GetProperty("roles")[0].GetString().ShouldBe("LoanOfficer");

        // Update group roles → reassign → delete group.
        var updated = await client.PutAsJsonAsync(
            $"/api/admin/tenants/{tenant}/groups/{gid}",
            new
            {
                name = "loan-officers",
                description = (string?)null,
                roles = new[] { "LoanOfficer", "TenantAdmin" }
            });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await updated.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("roles").GetArrayLength().ShouldBe(2);

        var removed = await client.DeleteAsync(
            $"/api/admin/tenants/{tenant}/groups/{gid}/members/{uid}");
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await removed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("memberCount").GetInt32().ShouldBe(0);

        (await client.DeleteAsync($"/api/admin/tenants/{tenant}/groups/{gid}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var groups = await client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/tenants/{tenant}/groups");
        groups.EnumerateArray().Any(g => g.GetProperty("id").GetString() == gid)
            .ShouldBeFalse();
    }

    /// Full dev PKCE dance → opaque access token for a seeded user.
    private async Task<string> LoginAsync(
        string tenant, LolaRole role, string tenantStatus = "active")
    {
        var email = $"{tenant}-{role}@test.dev";
        await factory.SeedUserAsync(
            tenant, email, Password, role, tenantStatus: tenantStatus);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = "/connect/authorize?response_type=code" +
            "&client_id=lola-officer-console" +
            "&redirect_uri=http%3A%2F%2Flocalhost%3A3200%2Fcallback" +
            "&scope=openid%20profile%20lola-api%20offline_access" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        await client.GetAsync(authorize);
        await client.PostAsync("/account/login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = Password,
                ["returnUrl"] = authorize
            }));
        var second = await client.GetAsync(authorize);
        var location = second.Headers.Location!.ToString();
        var code = Uri.UnescapeDataString(location.Split("code=")[1].Split('&')[0]);

        var tokens = await client.PostAsync("/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = "lola-officer-console",
                ["redirect_uri"] = "http://localhost:3200/callback",
                ["code"] = code,
                ["code_verifier"] = verifier
            }));
        return (await tokens.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
