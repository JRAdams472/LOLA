using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-286: login is tenant-scoped. The same email may exist under two
/// tenants; the org hint picks the directory — there is no global
/// lookup. Control-plane identity columns are ciphertext + blind index.
public sealed class TenantScopedLoginTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Same_email_in_two_tenants_resolves_by_org_hint()
    {
        const string email = "shared@test.dev";
        var tenantA = $"t-{Guid.NewGuid():N}"[..13];
        var tenantB = $"t-{Guid.NewGuid():N}"[..13];
        await factory.SeedUserAsync(tenantA, email, PkceLogin.Password);
        await factory.SeedUserAsync(tenantB, email, PkceLogin.Password);

        var tokenA = await TokenViaLoginAsync(tenantA, email);
        var tokenB = await TokenViaLoginAsync(tenantB, email);

        (await TidAsync(tokenA)).ShouldBe(tenantA);
        (await TidAsync(tokenB)).ShouldBe(tenantB);
    }

    [Fact]
    public async Task Login_with_unknown_org_is_denied()
    {
        var tenant = $"t-{Guid.NewGuid():N}"[..13];
        var email = $"{tenant}@test.dev";
        await factory.SeedUserAsync(tenant, email, PkceLogin.Password);

        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true
            });
        var resp = await client.PostAsync("/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["org"] = $"nope-{Guid.NewGuid():N}"[..16],
                ["email"] = email,
                ["password"] = PkceLogin.Password,
                ["returnUrl"] = "/"
            }));
        // Bounced back to the form — never an authenticated redirect
        // to returnUrl.
        resp.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().ShouldContain("/account/login");
    }

    [Fact]
    public async Task Control_plane_stores_identity_as_ciphertext_and_index()
    {
        var tenant = $"t-{Guid.NewGuid():N}"[..13];
        var email = $"{tenant}@test.dev";
        await factory.SeedUserAsync(tenant, email, PkceLogin.Password);

        var db = factory.Services.GetRequiredService<ControlPlaneDb>();
        using var conn = db.Open();
        var row = await conn.QuerySingleAsync<(
            string Email, string? EmailIdx, string DisplayName)>(
            "SELECT email AS Email, email_idx AS EmailIdx, " +
            "display_name AS DisplayName FROM users " +
            "WHERE tenant_id = @t", new { t = tenant });

        row.Email.ShouldNotBe(email);
        row.Email.ShouldStartWith("vault:");
        row.EmailIdx.ShouldNotBeNull();
        row.EmailIdx.ShouldStartWith("idx:");
        row.DisplayName.ShouldNotBe("Test User");
    }

    private async Task<string> TokenViaLoginAsync(string org, string email)
    {
        using var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true
            });
        var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = B64Url(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = "/connect/authorize?response_type=code" +
            "&client_id=lola-officer-console" +
            $"&redirect_uri={Uri.EscapeDataString("http://localhost:3200/callback")}" +
            "&scope=openid%20profile%20lola-api" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        await client.GetAsync(authorize);
        var login = await client.PostAsync("/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["org"] = org,
                ["email"] = email,
                ["password"] = PkceLogin.Password,
                ["returnUrl"] = authorize
            }));
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        var second = await client.GetAsync(authorize);
        var location = second.Headers.Location!.ToString();
        var code = Uri.UnescapeDataString(
            location.Split("code=")[1].Split('&')[0]);

        var tokens = await client.PostAsync("/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = "lola-officer-console",
                ["redirect_uri"] = "http://localhost:3200/callback",
                ["code"] = code,
                ["code_verifier"] = verifier
            }));
        var json = JsonDocument.Parse(
            await tokens.Content.ReadAsStringAsync()).RootElement;
        return json.GetProperty("access_token").GetString()!;
    }

    // Access tokens are opaque references — the tid claim is proven
    // through userinfo, same as OidcFlowTests.
    private async Task<string> TidAsync(string accessToken)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", accessToken);
        var info = JsonDocument.Parse(await client
            .GetStringAsync("/connect/userinfo")).RootElement;
        return info.GetProperty("tid").GetString()!;
    }

    private static string B64Url(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
