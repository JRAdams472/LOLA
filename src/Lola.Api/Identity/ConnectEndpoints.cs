using System.Net;
using System.Security.Claims;
using Lola.Application.Identity;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Lola.Api.Identity;

/// OpenIddict passthrough endpoints + the minimal dev login form.
/// The login page is a placeholder until the M11/M12 UIs exist — it
/// verifies credentials (Argon2) and issues the login cookie the
/// authorize endpoint consumes.
public static class ConnectEndpoints
{
    public const string LoginScheme = "LolaLogin";

    public static void MapConnectEndpoints(this WebApplication app)
    {
        // --- authorization code + PKCE (passthrough) -------------------
        app.MapGet("/connect/authorize", async (HttpContext ctx) =>
        {
            var auth = await ctx.AuthenticateAsync(LoginScheme);
            if (!auth.Succeeded)
            {
                var returnUrl = ctx.Request.Path + ctx.Request.QueryString;
                return Results.Redirect(
                    $"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            var request = ctx.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("missing oidc request");
            var identity = new ClaimsIdentity(
                auth.Principal.Claims,
                OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            identity.SetScopes(request.GetScopes());
            identity.SetResources("lola");
            foreach (var claim in identity.Claims)
                claim.SetDestinations(Destinations.AccessToken, Destinations.IdentityToken);
            return Results.SignIn(
                new ClaimsPrincipal(identity),
                authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        });

        // --- token (passthrough): client credentials + code + refresh --
        app.MapPost("/connect/token", async (HttpContext ctx) =>
        {
            var request = ctx.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("missing oidc request");

            if (request.IsClientCredentialsGrantType())
            {
                // Service account — no user behind it. Per-tenant
                // `svc-{tenant}` clients carry `tid` so their tokens
                // scope to their own tenant's surfaces only (MPE-87).
                var identity = new ClaimsIdentity(
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                identity.AddClaim(new Claim(Claims.Subject, request.ClientId!));
                identity.AddClaim(new Claim(Claims.Name, request.ClientId!));
                // lola-ops is the platform operator (MPE-91): its tokens
                // carry the ops role, not Service — the global registry
                // surface keys on it via the PlatformOps policy.
                identity.AddClaim(new Claim(Claims.Role,
                    request.ClientId == "lola-ops" ? "lola-ops" : "Service"));
                if (request.ClientId?.StartsWith("svc-", StringComparison.Ordinal) == true
                    && request.ClientId.Length > 4)
                    identity.AddClaim(new Claim("tid", request.ClientId[4..]));
                identity.SetScopes(request.GetScopes());
                identity.SetResources("lola");
                foreach (var claim in identity.Claims)
                    claim.SetDestinations(Destinations.AccessToken);
                return Results.SignIn(
                    new ClaimsPrincipal(identity),
                    authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            {
                // Principal is restored from the authorization code or
                // refresh token presented by the client.
                var result = await ctx.AuthenticateAsync(
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                if (!result.Succeeded || result.Principal is null)
                {
                    return Results.Forbid(authenticationSchemes:
                        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
                }

                var identity = new ClaimsIdentity(
                    result.Principal.Claims,
                    OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                identity.SetResources("lola");
                foreach (var claim in identity.Claims)
                    claim.SetDestinations(Destinations.AccessToken, Destinations.IdentityToken);
                return Results.SignIn(
                    new ClaimsPrincipal(identity),
                    authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "unsupported grant type");
        }).RequireRateLimiting("auth-tight");

        // --- userinfo (passthrough) ------------------------------------
        app.MapGet("/connect/userinfo", (HttpContext ctx) =>
        {
            var p = ctx.User;
            if (p.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();
            return Results.Json(new Dictionary<string, object?>
            {
                [Claims.Subject] = p.FindFirst(Claims.Subject)?.Value,
                [Claims.Name] = p.FindFirst(Claims.Name)?.Value,
                ["tid"] = p.FindFirst("tid")?.Value,
                ["roles"] = p.FindAll(Claims.Role).Select(c => c.Value).ToArray()
            });
        }).RequireAuthorization();

        // --- end session ------------------------------------------------
        app.MapGet("/connect/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(LoginScheme);
            return Results.SignOut(
                authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme],
                properties: null);
        });

        // --- dev login form ----------------------------------------------
        // The IdP-side credential page the OIDC flows bounce through —
        // styled to the console's GitHub-dark world; `err` marks a failed
        // attempt. Credentials verify via Argon2 and set the lola.auth
        // cookie /connect/authorize consumes.
        // CSP is `default-src 'self'` — an inline <style> block is refused,
        // so the page pulls its stylesheet from a same-origin route.
        app.MapGet("/account/login.css", () => Results.Content(LoginCss, "text/css"));

        app.MapGet("/account/login", (string? returnUrl, int? err) => Results.Content($$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Sign in — LOLA</title>
            <link rel="stylesheet" href="/account/login.css"></head><body>
            <form class="card" method="post" action="/account/login">
              <h1>LOLA</h1>
              <p class="sub">Officer console — sign in to continue</p>
              {{(err is not null ? "<p class=\"err\">Invalid email or password.</p>" : "")}}
              <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl ?? "/")}}" />
              <label>Email<input name="email" type="email" required autocomplete="username" autofocus /></label>
              <label>Password<input name="password" type="password" required autocomplete="current-password" /></label>
              <button type="submit">Sign in</button>
            </form></body></html>
            """, "text/html"));

        app.MapPost("/account/login", async (HttpContext ctx,
            IUserDirectory users, IPasswordHasher hasher) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            var user = await users.FindByEmailAsync(email, ctx.RequestAborted);
            if (user?.PasswordHash is null || user.Status != Domain.Identity.UserStatus.Active ||
                !hasher.Verify(password, user.PasswordHash))
                return Results.Redirect(
                    $"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}&err=1");

            var roles = await users.RolesForUserAsync(user.Id, ctx.RequestAborted);
            var claims = new List<Claim>
            {
                new(Claims.Subject, user.Id),
                new(Claims.Name, user.DisplayName),
                new("tid", user.TenantId),
                new(Claims.Email, user.Email)
            };
            claims.AddRange(roles.Select(r => new Claim(Claims.Role, r.ToString())));

            await ctx.SignInAsync(LoginScheme, new ClaimsPrincipal(
                new ClaimsIdentity(claims, LoginScheme)),
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
                });

            // Only local redirects — never an open redirect.
            return Results.Redirect(
                returnUrl.StartsWith('/') ? returnUrl : "/");
        }).RequireRateLimiting("auth-tight");
    }

    private const string LoginCss = """
        * { box-sizing: border-box }
        body { margin: 0; min-height: 100vh; display: grid; place-items: center;
               background: #0d1117; color: #e6edf3;
               font: 14px/1.5 system-ui, -apple-system, Segoe UI, sans-serif }
        .card { width: 320px; padding: 28px; background: #161b22;
                border: 1px solid #30363d; border-radius: 8px }
        h1 { margin: 0 0 4px; font-size: 20px; color: #2f9cff }
        .sub { margin: 0 0 20px; color: #9198a1; font-size: 13px }
        label { display: block; margin-bottom: 14px; font-size: 13px }
        input { display: block; width: 100%; margin-top: 4px; padding: 8px 10px;
                background: #0d1117; border: 1px solid #30363d; border-radius: 6px;
                color: #e6edf3; font-size: 14px }
        input:focus { outline: none; border-color: #2f9cff }
        button { width: 100%; padding: 9px; border: 0; border-radius: 6px;
                 background: #2f9cff; color: #04121f; font-weight: 600;
                 font-size: 14px; cursor: pointer }
        button:hover { background: #58a6ff }
        .err { margin: 0 0 14px; padding: 8px 10px; border: 1px solid #f85149;
               border-radius: 6px; color: #f85149; font-size: 13px }
        """;
}
