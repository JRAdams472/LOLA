using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lola.Api.Tests;

/// The full dev PKCE dance — authorize → login → code → token — shared
/// by every test that needs a real access token (was duplicated as a
/// private helper in each test class).
public static class PkceLogin
{
    public const string Password = "correct horse battery staple";

    public static async Task<string> TokenAsync(
        LolaApiFactory factory, string tenant, LolaRole role,
        string clientId = "lola-officer-console",
        string redirectUri = "http://localhost:3200/callback",
        string? email = null, bool seed = true,
        string tenantStatus = "active")
    {
        email ??= $"{tenant}-{role}@test.dev";
        if (seed)
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
            $"&client_id={clientId}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&scope=openid%20profile%20lola-api%20offline_access" +
            $"&code_challenge={challenge}&code_challenge_method=S256";

        await client.GetAsync(authorize);
        await PostWithRetryAsync(client, "/account/login",
            new Dictionary<string, string>
            {
                ["email"] = email,
                ["password"] = Password,
                ["returnUrl"] = authorize
            });
        var second = await client.GetAsync(authorize);
        var location = second.Headers.Location!.ToString();
        var code = Uri.UnescapeDataString(location.Split("code=")[1].Split('&')[0]);

        var tokens = await PostWithRetryAsync(client, "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["code"] = code,
                ["code_verifier"] = verifier
            });
        return (await tokens.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }

    /// POST honoring the auth-tight 429 — the per-IP+path fixed
    /// window resets every minute and a rejected request consumes no
    /// permit, so polling until one frees is the correct client
    /// behavior (same contract real callers get). Parallel test
    /// classes share the in-memory host's budget, so bursts collide;
    /// 2s polls ride out a full window. Safe to retry: the wrapped
    /// calls (/connect/token, /account/login) are idempotent.
    public static async Task<HttpResponseMessage> PostWithRetryAsync(
        HttpClient client, string path, Dictionary<string, string> body)
    {
        for (var i = 0; ; i++)
        {
            var resp = await client.PostAsync(path,
                new FormUrlEncodedContent(body));
            if (resp.StatusCode != HttpStatusCode.TooManyRequests
                || i >= 40)
                return resp;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
