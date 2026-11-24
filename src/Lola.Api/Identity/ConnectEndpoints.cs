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
        app.MapGet("/connect/authorize", async (HttpContext ctx,
            IUserDirectory users, ITenantRegistry tenants) =>
        {
            var auth = await ctx.AuthenticateAsync(LoginScheme);
            if (!auth.Succeeded)
            {
                var returnUrl = ctx.Request.Path + ctx.Request.QueryString;
                return Results.Redirect(
                    $"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            // Revalidate the login cookie against the authority store
            // (MPE-275): a stale or legacy (versionless) cookie is signed
            // out and forced back through login rather than minting a
            // code carrying authority the user no longer holds.
            var live = await AuthorityGate.ValidateAsync(
                auth.Principal, users, tenants, ctx.RequestAborted);
            if (live is null)
            {
                await ctx.SignOutAsync(LoginScheme);
                var returnUrl = ctx.Request.Path + ctx.Request.QueryString;
                return Results.Redirect(
                    $"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            var request = ctx.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException("missing oidc request");
            // Re-stamp the CURRENT version so downstream code/refresh
            // checks compare against what was true at authorize time.
            var claims = auth.Principal.Claims
                .Where(c => c.Type != AuthorityGate.VersionClaim)
                .Append(new Claim(
                    AuthorityGate.VersionClaim, live.AuthVersion.ToString()));
            var identity = new ClaimsIdentity(
                claims,
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
        app.MapPost("/connect/token", async (HttpContext ctx,
            IUserDirectory users, ITenantRegistry tenants) =>
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
                // Platform authority binds to the validated client
                // registration (MPE-271): designated global clients only,
                // never a requested scope, role name or group membership.
                if (request.ClientId is not null
                    && PlatformAuthority.DesignatedClientIds.Contains(request.ClientId))
                    identity.AddClaim(new Claim(
                        PlatformAuthority.Claim, PlatformAuthority.Value));
                // BFF session-store clients get their namespace minted
                // into the token — the /bff-sessions surface keys every
                // lookup on it, so a handle from one client can never
                // resolve through the other's credentials (MPE-289).
                if (BffSessionEndpoints.NamespaceFor(request.ClientId) is { } bffNs)
                    identity.AddClaim(new Claim(
                        BffSessionEndpoints.NamespaceClaim, bffNs));
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

                // The code/refresh grant was minted under the version it
                // carries — deny redemption the moment authority moved
                // (MPE-275). Legacy grants carry no version and fail here.
                var live = await AuthorityGate.ValidateAsync(
                    result.Principal, users, tenants, ctx.RequestAborted);
                if (live is null)
                {
                    return Results.Forbid(authenticationSchemes:
                        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
                }

                var identity = new ClaimsIdentity(
                    result.Principal.Claims
                        .Where(c => c.Type != AuthorityGate.VersionClaim)
                        .Append(new Claim(
                            AuthorityGate.VersionClaim, live.AuthVersion.ToString())),
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
                // The caller's own authority version — the session-store
                // resolve bar (MPE-290) needs it to seed sessions.
                [AuthorityGate.VersionClaim] =
                    p.FindFirst(AuthorityGate.VersionClaim)?.Value,
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
              {{(err is not null ? "<p class=\"err\">Invalid sign-in details.</p>" : "")}}
              <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(returnUrl ?? "/")}}" />
              <label>Organization<input name="org" type="text" required autocomplete="organization" autofocus /></label>
              <label>Email<input name="email" type="email" required autocomplete="username" /></label>
              <label>Password<input name="password" type="password" required autocomplete="current-password" /></label>
              <button type="submit">Sign in</button>
            </form></body></html>
            """, "text/html"));

        /// Tenant-aware login (MPE-286, ADR-0005): the org slug resolves
        /// through the keyed `slug_idx`, then email resolves inside that
        /// tenant only via `email_idx` — there is no global identity
        /// lookup. Every failure (unknown org, unknown email, bad
        /// password, disabled user) redirects identically so the
        /// response can't oracle which leg was wrong.
        app.MapPost("/account/login", async (HttpContext ctx,
            IUserDirectory users, ITenantRegistry tenants,
            IPasswordHasher hasher) =>
        {
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var org = form["org"].ToString();
            var email = form["email"].ToString();
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            IResult Deny() => Results.Redirect(
                $"/account/login?returnUrl={Uri.EscapeDataString(returnUrl)}&err=1");

            var tenant = string.IsNullOrWhiteSpace(org) ? null
                : await tenants.FindBySlugAsync(org, ctx.RequestAborted);
            var user = tenant is null ? null
                : await users.FindByEmailInTenantAsync(tenant.Id, email, ctx.RequestAborted);
            if (user?.PasswordHash is null || user.Status != Domain.Identity.UserStatus.Active ||
                !hasher.Verify(password, user.PasswordHash))
                return Deny();

            var roles = await users.RolesForUserAsync(user.Id, ctx.RequestAborted);
            var claims = new List<Claim>
            {
                new(Claims.Subject, user.Id),
                new(Claims.Name, user.DisplayName),
                new("tid", user.TenantId),
                new(Claims.Email, user.Email),
                new(AuthorityGate.VersionClaim, user.AuthVersion.ToString())
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
