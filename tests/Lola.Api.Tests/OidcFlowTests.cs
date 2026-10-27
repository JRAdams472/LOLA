using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-85 acceptance: OIDC flows for both audiences + service
/// client-credentials + rotating refresh + tid/role claims.
public sealed class OidcFlowTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task Client_credentials_issues_token_and_rejects_bad_secret()
    {
        var client = factory.CreateClient();
        var ok = await TokenRequest(client, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "lola-svc",
            ["client_secret"] = "svc-secret-value",
            ["scope"] = "lola-api"
        });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ok.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString().ShouldNotBeNullOrEmpty();

        var bad = await TokenRequest(client, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "lola-svc",
            ["client_secret"] = "wrong"
        });
        bad.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("lola-officer-console", "http://localhost:3200/callback")]
    [InlineData("lola-borrower-portal", "http://localhost:3300/callback")]
    public async Task Pkce_flow_issues_rotating_tokens_with_tenant_claims(
        string clientId, string redirectUri)
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var email = $"{tenant}@test.dev";
        var user = await factory.SeedUserAsync(tenant, email, Password);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = $"/connect/authorize?response_type=code&client_id={clientId}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&scope=openid%20profile%20lola-api%20offline_access" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        // Unauthenticated → redirected to the login form.
        var first = await client.GetAsync(authorize);
        first.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        first.Headers.Location!.ToString().ShouldContain("/account/login");

        // Login (form post sets the auth cookie).
        var login = await client.PostAsync("/account/login",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = Password,
                ["returnUrl"] = authorize
            }));
        login.StatusCode.ShouldBe(HttpStatusCode.Redirect);

        // Authenticated authorize → redirect to client with code.
        var second = await client.GetAsync(authorize);
        second.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = second.Headers.Location!.ToString();
        location.ShouldStartWith(redirectUri);
        var code = Uri.UnescapeDataString(
            location.Split("code=")[1].Split('&')[0]);

        var tokens = await Exchange(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
            ["code_verifier"] = verifier
        });
        tokens.AccessToken.ShouldNotBeNullOrEmpty();
        tokens.RefreshToken.ShouldNotBeNullOrEmpty();
        tokens.IdToken.ShouldNotBeNullOrEmpty();

        // userinfo carries tid + role claims.
        client.DefaultRequestHeaders.Authorization =
            new("Bearer", tokens.AccessToken);
        var info = await client.GetFromJsonAsync<JsonElement>("/connect/userinfo");
        info.GetProperty("tid").GetString().ShouldBe(tenant);
        info.GetProperty("sub").GetString().ShouldBe(user.Id);
        info.GetProperty("roles")[0].GetString().ShouldBe("LoanOfficer");

        // Rotating refresh → new refresh token; reuse of old → rejected.
        var rotated = await Exchange(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = tokens.RefreshToken!
        });
        rotated.RefreshToken.ShouldNotBeNullOrEmpty();
        rotated.RefreshToken.ShouldNotBe(tokens.RefreshToken);

        var reused = await TokenRequest(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = tokens.RefreshToken!
        });
        reused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static async Task<HttpResponseMessage> TokenRequest(
        HttpClient client, Dictionary<string, string> form)
        => await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));

    private static async Task<(string? AccessToken, string? RefreshToken, string? IdToken)>
        Exchange(HttpClient client, Dictionary<string, string> form)
    {
        var resp = await TokenRequest(client, form);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK,
            await resp.Content.ReadAsStringAsync());
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("access_token").GetString(),
                json.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
                json.TryGetProperty("id_token", out var i) ? i.GetString() : null);
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
