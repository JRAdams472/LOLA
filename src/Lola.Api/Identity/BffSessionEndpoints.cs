using System.Net;
using Lola.Application.Identity;
using Lola.ControlPlane;
using Lola.Domain.Identity;

namespace Lola.Api.Identity;

/// Server-side BFF session custody (MPE-289 / remediation phase 9) —
/// the API owns the OAuth grant end-to-end: BFFs send an opaque handle
/// and get back a usable access token; the refresh token never leaves
/// this process. Callers authenticate as dedicated confidential
/// clients (`lola-bff-console` / `lola-bff-portal`) whose minted
/// `lola_bff_ns` claim pins the session namespace — a handle minted
/// under one BFF can never resolve through the other's credentials.
public static class BffSessionEndpoints
{
    public const string NamespaceClaim = "lola_bff_ns";
    public const string Scope = "lola-bff";
    public const string TokenClient = "bff-token";

    /// Refresh this far before the access-token expiry, ms — mirrors
    /// the margin the BFFs used when they held the tokens themselves.
    private const long RefreshMarginMs = 30_000;

    /// OIDC client that owns the user grant per namespace — refresh and
    /// revocation act on its behalf.
    private static readonly IReadOnlyDictionary<string, string> GrantClients =
        new Dictionary<string, string>
        {
            ["console"] = "lola-officer-console",
            ["portal"] = "lola-borrower-portal",
        };

    /// Namespace claim values ↔ grant owners, resolved from the
    /// caller's token — never from request input.
    public static string? NamespaceFor(string? clientId) => clientId switch
    {
        "lola-bff-console" => "console",
        "lola-bff-portal" => "portal",
        _ => null,
    };

    public static void MapBffSessionEndpoints(this WebApplication app)
    {
        // Server-to-server calls ride the bff-store credential check —
        // exempt from the global 200/min bucket so BFF resolve traffic
        // (one call per user request) can't starve browser-facing
        // endpoints that share it.
        var g = app.MapGroup("/bff-sessions")
            .RequireAuthorization(LolaPolicies.BffStore)
            .DisableRateLimiting();

        g.MapPost("/", async (
            HttpContext ctx, CreateRequest req, BffSessionStore store,
            CancellationToken ct) =>
        {
            var ns = ctx.User.FindFirst(NamespaceClaim)?.Value;
            if (ns is null || req.Handle is not { Length: > 0 and <= 128 }
                || req.Subject is not { Length: > 0 and <= 128 }
                || req.TenantId is not { Length: > 0 and <= 64 }
                || req.AccessToken is not { Length: > 0 })
                return Results.BadRequest();
            await store.CreateAsync(
                ns, BffSessionStore.HashHandle(req.Handle), req.Subject,
                req.TenantId, req.AuthVersion, req.AccessToken,
                req.RefreshToken, req.Name, req.AtExpMs, req.RtExpMs,
                req.AbsExpMs, req.IdleMs, NowMs(), ct);
            return Results.Created();
        });

        g.MapPost("/resolve", ResolveAsync);

        g.MapPost("/touch", async (
            HttpContext ctx, HandleRequest req, BffSessionStore store,
            CancellationToken ct) =>
        {
            var ns = ctx.User.FindFirst(NamespaceClaim)?.Value;
            return ns is not null && req.Handle is { Length: > 0 }
                && await store.TouchAsync(
                ns, BffSessionStore.HashHandle(req.Handle), NowMs(), ct)
                ? Results.NoContent()
                : Results.Unauthorized();
        });

        g.MapDelete("/{handle}", async (
            HttpContext ctx, string handle, BffSessionStore store,
            IHttpClientFactory http, IConfiguration config,
            CancellationToken ct) =>
        {
            var ns = ctx.User.FindFirst(NamespaceClaim)?.Value;
            if (ns is null) return Results.Unauthorized();
            var (found, rt) = await store.RevokeAsync(
                ns, BffSessionStore.HashHandle(handle), NowMs(), ct);
            if (found && rt is not null)
                await PostGrantAsync(http, config, "/connect/revoke",
                    new Dictionary<string, string>
                    {
                        ["token"] = rt,
                        ["token_type_hint"] = "refresh_token",
                        ["client_id"] = GrantClients[ns],
                    }, ct);
            return found ? Results.NoContent() : Results.NotFound();
        });
    }

    private static async Task<IResult> ResolveAsync(
        HttpContext ctx, HandleRequest req, BffSessionStore store,
        IUserDirectory users, ITenantRegistry tenants,
        IHttpClientFactory http, IConfiguration config, CancellationToken ct)
    {
        var ns = ctx.User.FindFirst(NamespaceClaim)?.Value;
        if (ns is null) return Results.Unauthorized();
        var hash = BffSessionStore.HashHandle(req.Handle);
        var s = await store.ReadAsync(ns, hash, ct);
        if (s is null || !Live(s, NowMs()))
            return Results.Unauthorized();
        if (!await AuthorityHoldsAsync(s, users, tenants, ct))
        {
            // Privilege moved under the session (phase 6): kill it and
            // deny — the next resolve can't resurrect it either.
            await store.MarkRevokedAsync(ns, hash, NowMs(), ct);
            return Results.Unauthorized();
        }
        var now = NowMs();
        if (now < s.AtExpMs - RefreshMarginMs)
        {
            // Resolving is live traffic — slide the idle window
            // (throttled), never the absolute bound.
            await store.SlideIdleAsync(ns, hash, now, ct);
            return Results.Ok(new ResolvedResponse(
                s.AccessToken, s.AtExpMs, s.Subject, s.Name));
        }
        if (s.RefreshToken is null || (s.RtExpMs is { } re && now >= re))
        {
            await store.MarkRevokedAsync(ns, hash, now, ct);
            return Results.Unauthorized();
        }

        // Single-flight: contenders queue on the handle gate and re-read
        // — the loser sees the winner's rotated row instead of redeeming
        // a dead rt (reuse detection would kill the session over it).
        var gate = BffSessionStore.GateFor(hash);
        await gate.WaitAsync(ct);
        try
        {
            s = await store.ReadAsync(ns, hash, ct);
            now = NowMs();
            if (s is null || !Live(s, now))
                return Results.Unauthorized();
            if (now < s.AtExpMs - RefreshMarginMs)
                return Results.Ok(new ResolvedResponse(
                    s.AccessToken, s.AtExpMs, s.Subject, s.Name));

            var tokens = await PostGrantAsync(http, config, "/connect/token",
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = GrantClients[ns],
                    ["refresh_token"] = s.RefreshToken!,
                }, ct);
            if (tokens is null)
                return Results.StatusCode((int)HttpStatusCode.ServiceUnavailable);
            if (tokens.Error is not null)
            {
                // invalid_grant = the refresh grant is definitively dead
                // (revoked/rotated past us) — the session dies with it.
                if (tokens.Error == "invalid_grant")
                    await store.MarkRevokedAsync(ns, hash, now, ct);
                return Results.Unauthorized();
            }
            var rt = tokens.RefreshToken ?? s.RefreshToken;
            var rtExp = RtExpiryMs(tokens, rt) ?? s.RtExpMs;
            var atExp = now + (tokens.ExpiresIn ?? 3600) * 1000L;
            if (!await store.RotateAsync(
                    ns, hash, s.Rev, tokens.AccessToken!, rt,
                    s.Name, atExp, rtExp, now, ct))
            {
                // Lost a rotation race (shouldn't happen under the gate)
                // — re-read converges on whoever won.
                s = await store.ReadAsync(ns, hash, ct);
                return s is not null && Live(s, now)
                    ? Results.Ok(new ResolvedResponse(
                        s.AccessToken, s.AtExpMs, s.Subject, s.Name))
                    : Results.Unauthorized();
            }
            return Results.Ok(new ResolvedResponse(
                tokens.AccessToken!, atExp, s.Subject, s.Name));
        }
        finally
        {
            gate.Release();
        }
    }

    /// Revoked/expired rows deny — expiry paths also stamp the revoke so
    /// a stale row can't be replayed past its bound.
    private static bool Live(BffSession s, long nowMs) =>
        s.RevokedAtMs is null
        && nowMs < s.AbsExpMs
        && (s.IdleMs is null || nowMs - s.LastActiveMs <= s.IdleMs);

    /// Phase-6 coordination: the stored auth_version must match the
    /// live directory version and the user + tenant must still be
    /// active — same bar AuthorityGate holds grants to.
    private static async Task<bool> AuthorityHoldsAsync(
        BffSession s, IUserDirectory users, ITenantRegistry tenants,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(s.Subject, ct);
        if (user is null || user.Status != UserStatus.Active
            || user.AuthVersion != s.AuthVersion)
            return false;
        var tenant = await tenants.FindByIdAsync(user.TenantId, ct);
        return tenant?.Status == "active";
    }

    private static long? RtExpiryMs(TokenResponse t, string? rt)
    {
        if (t.RefreshExpiresIn is { } secs)
            return NowMs() + secs * 1000L;
        if (rt is not null)
        {
            try
            {
                var jwt = rt.Split('.');
                if (jwt.Length == 3)
                {
                    var payload = System.Text.Json.JsonDocument.Parse(
                        Base64UrlDecode(jwt[1]));
                    if (payload.RootElement.TryGetProperty("exp", out var e))
                        return e.GetInt64() * 1000L;
                }
            }
            catch { /* opaque rt — expiry not determinable */ }
        }
        return null;
    }

    private static byte[] Base64UrlDecode(string s) =>
        Convert.FromBase64String(
            s.Replace('-', '+').Replace('_', '/')
                .PadRight(s.Length + (4 - s.Length % 4) % 4, '='));

    /// One form POST to our own connect surface — never throws;
    /// transport failure → null (deny), non-2xx → Error populated.
    private static async Task<TokenResponse?> PostGrantAsync(
        IHttpClientFactory http, IConfiguration config, string path,
        Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            var res = await http.CreateClient(TokenClient).PostAsync(
                TokenUrl(config, path), new FormUrlEncodedContent(form), ct);
            var body = await res.Content
                .ReadAsStringAsync(ct);
            var doc = string.IsNullOrWhiteSpace(body)
                ? null
                : System.Text.Json.JsonSerializer.Deserialize<TokenResponse>(body,
                    new System.Text.Json.JsonSerializerOptions
                    { PropertyNameCaseInsensitive = true });
            if (!res.IsSuccessStatusCode)
                return new TokenResponse { Error = doc?.Error ?? "http_error" };
            return doc ?? new TokenResponse { Error = "empty_response" };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// Self-call base: explicit `BFF_API_BASE` (tests/compose) else the
    /// configured issuer, else the dev default — never the request
    /// Host header, which a poisoned value could aim at an attacker.
    private static string TokenUrl(IConfiguration config, string path) =>
        $"{config["BFF_API_BASE"] ?? config["OIDC_ISSUER"]
            ?? "http://localhost:8200"}{path}";

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public sealed record CreateRequest(
        string Handle, string Subject, string TenantId, long AuthVersion,
        string AccessToken, string? RefreshToken, string? Name,
        long AtExpMs, long? RtExpMs, long AbsExpMs, long? IdleMs);

    public sealed record HandleRequest(string Handle);

    /// Response carries the usable access token only — the refresh
    /// grant never leaves the store (custody is the point).
    public sealed record ResolvedResponse(
        string AccessToken, long AtExpMs, string Subject, string? Name);

    private sealed class TokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("refresh_expires_in")]
        public long? RefreshExpiresIn { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public long? ExpiresIn { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("id_token")]
        public string? IdToken { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public string? Error { get; init; }
    }
}
