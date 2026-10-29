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
        app.MapGet("/account/login", (string? returnUrl) => Results.Content($"""
            <!doctype html><html><body>
            <form method="post" action="/account/login">
              <input type="hidden" name="returnUrl" value="{WebUtility.HtmlEncode(returnUrl ?? "/")}" />
              <label>Email <input name="email" type="email" required /></label>
              <label>Password <input name="password" type="password" required /></label>
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
}
