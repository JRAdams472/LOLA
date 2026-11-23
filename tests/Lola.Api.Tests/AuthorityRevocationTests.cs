using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Lola.Api.Identity;
using Lola.Application.Identity;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Lola.Api.Tests;

/// MPE-275 acceptance: a committed authority change (disable,
/// membership removal, group-role edit, group deletion) denies the
/// next protected request on ALL grant paths — the login cookie at
/// /connect/authorize, outstanding reference access tokens, refresh
/// tokens, and unredeemed codes. Unaffected users and service clients
/// keep working; legacy version-less grants require re-login.
public sealed class AuthorityRevocationTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private const string ClientId = "lola-officer-console";
    private const string RedirectUri = "http://localhost:3200/callback";

    [Theory]
    [InlineData("disable")]
    [InlineData("membership")]
    [InlineData("role-edit")]
    [InlineData("group-delete")]
    public async Task Committed_authority_change_denies_all_grant_paths(string change)
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var creds = await MintAsync(tenant);
        var groupId = await GroupIdOfAsync(creds.User.Id);

        // Prove all paths are live BEFORE the change.
        (await AuthorizeAsync(creds.Client)).Headers.Location!.ToString()
            .ShouldStartWith(RedirectUri);
        await AssertBearerOkAsync(creds.AccessToken);

        await ApplyChangeAsync(change, creds.User, groupId);

        // Bearer — the reference-token row was revoked in the bump
        // transaction, so the very next call is denied.
        var bearer = factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization =
            new("Bearer", creds.AccessToken);
        (await bearer.GetAsync("/connect/userinfo"))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Refresh — the revoked grant cannot mint a new pair.
        var refreshed = await PkceLogin.PostWithRetryAsync(
            factory.CreateClient(), "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = creds.RefreshToken
            });
        refreshed.StatusCode.ShouldBeOneOf(
            HttpStatusCode.BadRequest, HttpStatusCode.Forbidden,
            HttpStatusCode.Unauthorized);

        // Cookie — the stale login cookie is signed out and bounced
        // back to the login page instead of minting another code.
        var bounced = await AuthorizeAsync(creds.Client);
        bounced.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        bounced.Headers.Location!.ToString().ShouldContain("/account/login");
    }

    [Fact]
    public async Task Unredeemed_code_cannot_mint_removed_privileges()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var creds = await MintAsync(tenant);

        // A code minted while privileged but redeemed AFTER the
        // downgrade must not carry the old authority across.
        var (verifier, _, authorize) = await FreshAuthorizeChallengeAsync();
        var live = await creds.Client.GetAsync(authorize);
        var code = Uri.UnescapeDataString(
            live.Headers.Location!.ToString().Split("code=")[1].Split('&')[0]);

        var db = factory.Services.GetRequiredService<ControlPlaneDb>();
        await new UserDirectory(db, TestCrypto.Cipher, TestCrypto.Index).SetStatusAsync(creds.User.Id, "disabled");

        var redeem = await PkceLogin.PostWithRetryAsync(
            factory.CreateClient(), "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["redirect_uri"] = RedirectUri,
                ["code"] = code,
                ["code_verifier"] = verifier
            });
        redeem.StatusCode.ShouldBeOneOf(
            HttpStatusCode.BadRequest, HttpStatusCode.Forbidden,
            HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unaffected_user_and_service_client_keep_working()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var doomed = await MintAsync(tenant);
        var surviving = await MintAsync(tenant, "survivor");

        var db = factory.Services.GetRequiredService<ControlPlaneDb>();
        await new UserDirectory(db, TestCrypto.Cipher, TestCrypto.Index).SetStatusAsync(doomed.User.Id, "disabled");

        // The surviving user's cookie still mints, and their bearer
        // token still resolves.
        (await AuthorizeAsync(surviving.Client)).Headers.Location!.ToString()
            .ShouldStartWith(RedirectUri);
        await AssertBearerOkAsync(surviving.AccessToken);

        // Service clients carry no human authority — unaffected.
        var svc = await PkceLogin.PostWithRetryAsync(
            factory.CreateClient(), "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-svc",
                ["client_secret"] = "svc-secret-value",
                ["scope"] = "lola-api"
            });
        svc.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Gate_denies_missing_version_and_propagates_store_failure()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var user = await factory.SeedUserAsync(
            tenant, $"gate-{tenant}@test.dev", PkceLogin.Password);
        var db = factory.Services.GetRequiredService<ControlPlaneDb>();
        var users = new UserDirectory(db, TestCrypto.Cipher, TestCrypto.Index);
        var tenants = new TenantRegistry(db, TestCrypto.Cipher, TestCrypto.Index);

        // Legacy grant shape — no version claim at all.
        var legacy = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(Claims.Subject, user.Id)], "test"));
        (await AuthorityGate.ValidateAsync(legacy, users, tenants, default))
            .ShouldBeNull();

        // Disabled user, matching version — still denied.
        var disabled = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(Claims.Subject, user.Id),
            new Claim(AuthorityGate.VersionClaim, "1")
        ], "test"));
        await users.SetStatusAsync(user.Id, "disabled");
        (await AuthorityGate.ValidateAsync(disabled, users, tenants, default))
            .ShouldBeNull();

        // A dead authority store throws — deny by error, never mint.
        var boom = new ThrowingUserDirectory();
        await Should.ThrowAsync<InvalidOperationException>(
            () => AuthorityGate.ValidateAsync(disabled, boom, tenants, default));
    }

    private sealed class ThrowingUserDirectory : IUserDirectory
    {
        public Task<LolaUser?> FindByIdAsync(string id, CancellationToken ct = default)
            => throw new InvalidOperationException("authority store offline");
        public Task<LolaUser?> FindByEmailInTenantAsync(string tenantId, string email, CancellationToken ct = default)
            => throw new InvalidOperationException("authority store offline");
        public Task<IReadOnlyList<LolaUser>> ListByTenantAsync(string tenantId, CancellationToken ct = default)
            => throw new InvalidOperationException("authority store offline");
        public Task<IReadOnlyList<LolaRole>> RolesForUserAsync(string userId, CancellationToken ct = default)
            => throw new InvalidOperationException("authority store offline");
        public Task InsertAsync(LolaUser user, CancellationToken ct = default)
            => throw new InvalidOperationException("authority store offline");
        public Task UpdatePasswordAsync(string userId, string passwordHash, string status, CancellationToken ct = default)
            => throw new InvalidOperationException("authority store offline");
        public Task SetStatusAsync(string userId, string status, CancellationToken ct = default)
            => throw new InvalidOperationException("authority store offline");
    }

    private async Task ApplyChangeAsync(
        string change, LolaUser user, string groupId)
    {
        var db = factory.Services.GetRequiredService<ControlPlaneDb>();
        switch (change)
        {
            case "disable":
                await new UserDirectory(db, TestCrypto.Cipher, TestCrypto.Index).SetStatusAsync(user.Id, "disabled");
                break;
            case "membership":
                await new GroupDirectory(db, TestCrypto.Cipher, TestCrypto.Index).RemoveMemberAsync(groupId, user.Id);
                break;
            case "role-edit":
                await new GroupDirectory(db, TestCrypto.Cipher, TestCrypto.Index)
                    .SetRolesAsync(groupId, [LolaRole.Borrower]);
                break;
            case "group-delete":
                await new GroupDirectory(db, TestCrypto.Cipher, TestCrypto.Index).DeleteAsync(groupId);
                break;
        }
    }

    private async Task<string> GroupIdOfAsync(string userId)
    {
        var db = factory.Services.GetRequiredService<ControlPlaneDb>();
        using var conn = db.Open();
        return await conn.QuerySingleAsync<string>(
            "SELECT group_id FROM user_groups WHERE user_id = @userId",
            new { userId });
    }

    private async Task AssertBearerOkAsync(string accessToken)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);
        (await c.GetAsync("/connect/userinfo"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<HttpResponseMessage> AuthorizeAsync(HttpClient client)
    {
        var (_, _, url) = await FreshAuthorizeChallengeAsync();
        return await client.GetAsync(url);
    }

    private static Task<(string Verifier, string Challenge, string Url)>
        FreshAuthorizeChallengeAsync()
    {
        var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var url = "/connect/authorize?response_type=code" +
            $"&client_id={ClientId}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            "&scope=openid%20profile%20lola-api%20offline_access" +
            $"&code_challenge={challenge}&code_challenge_method=S256";
        return Task.FromResult((verifier, challenge, url));
    }

    private async Task<(HttpClient Client, LolaUser User,
        string AccessToken, string RefreshToken)> MintAsync(
        string tenant, string tag = "rev")
    {
        var email = $"{tag}-{tenant}@test.dev";
        var user = await factory.SeedUserAsync(
            tenant, email, PkceLogin.Password);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var (verifier, _, authorize) = await FreshAuthorizeChallengeAsync();
        await client.GetAsync(authorize);
        await PkceLogin.PostWithRetryAsync(client, "/account/login",
            new Dictionary<string, string>
            {
                ["org"] = tenant,
                ["email"] = email,
                ["password"] = PkceLogin.Password,
                ["returnUrl"] = authorize
            });
        var granted = await client.GetAsync(authorize);
        var location = granted.Headers.Location!.ToString();
        location.ShouldStartWith(RedirectUri);
        var code = Uri.UnescapeDataString(
            location.Split("code=")[1].Split('&')[0]);

        var tokens = await PkceLogin.PostWithRetryAsync(client, "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = ClientId,
                ["redirect_uri"] = RedirectUri,
                ["code"] = code,
                ["code_verifier"] = verifier
            });
        var json = await tokens.Content.ReadFromJsonAsync<JsonElement>();
        return (client, user,
            json.GetProperty("access_token").GetString()!,
            json.GetProperty("refresh_token").GetString()!);
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
