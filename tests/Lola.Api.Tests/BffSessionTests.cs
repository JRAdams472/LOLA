using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Lola.Api.Identity;
using Lola.Application.Identity;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-289 acceptance: server-side BFF session custody — opaque handles
/// resolve to API-held tokens through the dedicated confidential
/// clients only. Namespace isolation, expiry/revocation deny,
/// single-flight rotation, and the refresh grant never leaves the API.
public sealed class BffSessionTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private const string ConsoleSecret = "bff-console-secret-value";
    private const string PortalSecret = "bff-portal-secret-value";

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string NewHandle() => Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static async Task<string> BffTokenAsync(
        HttpClient client, string clientId, string secret,
        string scope = "lola-bff")
    {
        var resp = await PkceLogin.PostWithRetryAsync(client, "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["scope"] = scope,
            });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }

    private static HttpRequestMessage Call(
        HttpMethod method, string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private async Task<HttpResponseMessage> CreateSessionAsync(
        HttpClient client, string token, string handle, LolaUser user,
        string at = "at-under-custody", string? rt = "rt-under-custody",
        long? idleMs = 1_800_000, long? absMs = null)
    {
        return await client.SendAsync(Call(HttpMethod.Post, "/bff-sessions",
            token, new
            {
                handle,
                subject = user.Id,
                tenantId = user.TenantId,
                authVersion = user.AuthVersion,
                accessToken = at,
                refreshToken = rt,
                name = "Test User",
                atExpMs = NowMs() + 3_600_000,
                rtExpMs = NowMs() + 86_400_000,
                absExpMs = absMs ?? NowMs() + 14_400_000,
                idleMs,
            }));
    }

    private async Task<LolaUser> UserAsync(string tag)
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        return await factory.SeedUserAsync(tenant, $"{tag}-{tenant}@test.dev",
            PkceLogin.Password);
    }

    private async Task ExecSqlAsync(string sql, object p)
    {
        var db = factory.Services.GetRequiredService<ControlPlaneDb>();
        using var conn = db.Open();
        await conn.ExecuteAsync(sql, p);
    }

    private static string HashOf(string handle) =>
        BffSessionStore.HashHandle(handle);

    [Fact]
    public async Task Create_then_resolve_returns_access_token_only()
    {
        var client = factory.CreateClient();
        var token = await BffTokenAsync(client, "lola-bff-console", ConsoleSecret);
        var user = await UserAsync("rt");
        var handle = NewHandle();

        (await CreateSessionAsync(client, token, handle, user)).StatusCode
            .ShouldBe(HttpStatusCode.Created);

        var resolved = await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/resolve", token, new { handle }));
        resolved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resolved.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("accessToken").GetString()
            .ShouldBe("at-under-custody");
        json.RootElement.GetProperty("subject").GetString()
            .ShouldBe(user.Id);
        // The refresh grant must never appear in a resolve response.
        body.ShouldNotContain("rt-under-custody");
        body.ShouldNotContain("refreshToken");
    }

    [Fact]
    public async Task Unknown_handle_denies()
    {
        var client = factory.CreateClient();
        var token = await BffTokenAsync(client, "lola-bff-console", ConsoleSecret);
        var resolved = await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/resolve", token, new { handle = NewHandle() }));
        resolved.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_namespace_handle_never_resolves()
    {
        var client = factory.CreateClient();
        var console = await BffTokenAsync(client, "lola-bff-console", ConsoleSecret);
        var portal = await BffTokenAsync(client, "lola-bff-portal", PortalSecret);
        var user = await UserAsync("ns");
        var handle = NewHandle();
        (await CreateSessionAsync(client, console, handle, user)).StatusCode
            .ShouldBe(HttpStatusCode.Created);

        // Same handle through the portal's credentials: not found —
        // the caller's minted namespace scopes the lookup.
        var resolved = await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/resolve", portal, new { handle }));
        resolved.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoke_denies_and_logout_path_kills_the_grant()
    {
        var client = factory.CreateClient();
        var token = await BffTokenAsync(client, "lola-bff-console", ConsoleSecret);
        var (user, _, rt) = await MintGrantAsync();
        var handle = NewHandle();
        (await CreateSessionAsync(client, token, handle, user, rt: rt))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await client.SendAsync(Call(HttpMethod.Delete,
            $"/bff-sessions/{handle}", token)))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/resolve", token, new { handle })))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The upstream refresh grant is dead too — re-presenting it
        // gets invalid_grant, not a rotation.
        var reuse = await PkceLogin.PostWithRetryAsync(client, "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = "lola-officer-console",
                ["refresh_token"] = rt,
            });
        (await reuse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().ShouldBe("invalid_grant");

        // Idempotent delete — unknown/already-dead is NotFound.
        (await client.SendAsync(Call(HttpMethod.Delete,
            $"/bff-sessions/{handle}", token)))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Expired_bounds_deny_and_revoke_the_row()
    {
        var client = factory.CreateClient();
        var token = await BffTokenAsync(client, "lola-bff-console", ConsoleSecret);
        var user = await UserAsync("exp");

        // Absolute bound in the past.
        var absHandle = NewHandle();
        (await CreateSessionAsync(client, token, absHandle, user,
            absMs: NowMs() - 1)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/resolve", token, new { handle = absHandle })))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Idle bound passed — stale last_active inside a live window.
        var idleHandle = NewHandle();
        (await CreateSessionAsync(client, token, idleHandle, user,
            idleMs: 60_000)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await ExecSqlAsync(
            "UPDATE bff_sessions SET last_active_ms = 0 WHERE handle_hash = @h",
            new { h = HashOf(idleHandle) });
        (await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/resolve", token, new { handle = idleHandle })))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Auth_version_drift_denies_and_revokes()
    {
        var client = factory.CreateClient();
        var token = await BffTokenAsync(client, "lola-bff-console", ConsoleSecret);
        var user = await UserAsync("ver");
        var handle = NewHandle();
        (await CreateSessionAsync(client, token, handle, user)).StatusCode
            .ShouldBe(HttpStatusCode.Created);

        // Phase-6 bump: the stored version is now stale.
        await ExecSqlAsync(
            "UPDATE users SET auth_version = auth_version + 1 WHERE id = @id",
            new { id = user.Id });

        (await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/resolve", token, new { handle })))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // Deny stamped the revoke — the handle stays dead.
        (await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/resolve", token, new { handle })))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Expired_access_token_rotates_single_flight()
    {
        var client = factory.CreateClient();
        var token = await BffTokenAsync(client, "lola-bff-console", ConsoleSecret);
        var (user, _, rt) = await MintGrantAsync();
        var handle = NewHandle();
        (await CreateSessionAsync(client, token, handle, user, rt: rt))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        // Force the refresh path: stored AT is already past the margin.
        await ExecSqlAsync(
            "UPDATE bff_sessions SET at_exp_ms = @t WHERE handle_hash = @h",
            new { t = NowMs() - 1, h = HashOf(handle) });

        // Racing resolvers — one refresh, all converge on the rotated AT.
        var resolves = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.SendAsync(Call(HttpMethod.Post,
                "/bff-sessions/resolve", token, new { handle }))));
        var bodies = await Task.WhenAll(resolves.Select(async r =>
        {
            r.StatusCode.ShouldBe(HttpStatusCode.OK);
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync())
                .RootElement.GetProperty("accessToken").GetString()!;
        }));
        bodies.Distinct().Count().ShouldBe(1);
        bodies[0].ShouldNotBe("at-under-custody");

        // Exactly one rotation happened.
        (await ScalarAsync(
            "SELECT rev FROM bff_sessions WHERE handle_hash = @h",
            new { h = HashOf(handle) })).ShouldBe(1);
    }

    [Fact]
    public async Task Dead_refresh_grant_denies_and_revokes()
    {
        var client = factory.CreateClient();
        var token = await BffTokenAsync(client, "lola-bff-console", ConsoleSecret);
        var user = await UserAsync("dead");
        var handle = NewHandle();
        (await CreateSessionAsync(client, token, handle, user))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await ExecSqlAsync(
            "UPDATE bff_sessions SET at_exp_ms = @t WHERE handle_hash = @h",
            new { t = NowMs() - 1, h = HashOf(handle) });

        // Stored rt was never real — the IdP answers invalid_grant and
        // the session must die, not retry on faith.
        (await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/resolve", token, new { handle })))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var revoked = await ScalarAsync(
            "SELECT revoked_at_ms FROM bff_sessions WHERE handle_hash = @h",
            new { h = HashOf(handle) });
        revoked.ShouldNotBeNull();
    }

    [Fact]
    public async Task Touch_slides_idle_but_never_absolute()
    {
        var client = factory.CreateClient();
        var token = await BffTokenAsync(client, "lola-bff-console", ConsoleSecret);
        var user = await UserAsync("tch");
        var handle = NewHandle();
        var abs = NowMs() + 14_400_000;
        (await CreateSessionAsync(client, token, handle, user, absMs: abs))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/touch", token, new { handle })))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ScalarAsync(
            "SELECT abs_exp_ms FROM bff_sessions WHERE handle_hash = @h",
            new { h = HashOf(handle) })).ShouldBe(abs);
        // Unknown handle: deny, not silent success.
        (await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/touch", token, new { handle = NewHandle() })))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Non_bff_callers_are_denied()
    {
        var client = factory.CreateClient();
        var (_, at, _) = await MintGrantAsync();
        var handle = NewHandle();

        // User AT — authenticated but carries no namespace claim.
        (await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/resolve", at, new { handle })))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        // No credentials at all.
        var anon = await client.PostAsync("/bff-sessions/resolve",
            JsonContent.Create(new { handle }));
        anon.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // svc token — Service role but no lola_bff_ns.
        var svc = await BffTokenAsync(client, "lola-svc", "svc-secret-value",
            "lola-api");
        (await client.SendAsync(Call(HttpMethod.Post,
            "/bff-sessions/resolve", svc, new { handle })))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<long?> ScalarAsync(string sql, object p)
    {
        var db = factory.Services.GetRequiredService<ControlPlaneDb>();
        using var conn = db.Open();
        return await conn.ExecuteScalarAsync<long?>(sql, p);
    }

    /// Real auth-code+PKCE grant — the refresh rotation tests need an
    /// rt the IdP will actually honor.
    private async Task<(LolaUser User, string At, string Rt)> MintGrantAsync()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var email = $"bff-{tenant}@test.dev";
        var user = await factory.SeedUserAsync(tenant, email, PkceLogin.Password);
        var client = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = true,
            });
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var challenge = Convert.ToBase64String(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var authorize = "/connect/authorize?response_type=code" +
            "&client_id=lola-officer-console" +
            "&redirect_uri=" +
            Uri.EscapeDataString("http://localhost:3200/callback") +
            "&scope=openid%20profile%20lola-api%20offline_access" +
            $"&code_challenge={challenge}&code_challenge_method=S256";
        await client.GetAsync(authorize);
        await PkceLogin.PostWithRetryAsync(client, "/account/login",
            new Dictionary<string, string>
            {
                ["org"] = tenant,
                ["email"] = email,
                ["password"] = PkceLogin.Password,
                ["returnUrl"] = authorize,
            });
        var granted = await client.GetAsync(authorize);
        var code = Uri.UnescapeDataString(
            granted.Headers.Location!.ToString().Split("code=")[1].Split('&')[0]);
        var tokens = await PkceLogin.PostWithRetryAsync(client, "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = "lola-officer-console",
                ["redirect_uri"] = "http://localhost:3200/callback",
                ["code"] = code,
                ["code_verifier"] = verifier,
            });
        var json = await tokens.Content.ReadFromJsonAsync<JsonElement>();
        return (user,
            json.GetProperty("access_token").GetString()!,
            json.GetProperty("refresh_token").GetString()!);
    }
}
