using System.Security.Claims;
using Lola.Application.Identity;
using Lola.Domain.Identity;
using OpenIddict.Abstractions;

namespace Lola.Api.Identity;

/// Human-session revalidation (MPE-275): every grant mint or
/// continuation proves the subject still holds the authority the
/// credential was issued under — live user, active tenant, and an
/// auth-version claim matching the directory's current version.
/// Reads are uncached: a committed privilege change denies the very
/// next request on every grant path.
public static class AuthorityGate
{
    /// Claim carrying the auth version a cookie or grant was issued at.
    public const string VersionClaim = "lola_auth_ver";

    /// Returns the live user when the presented credential may still
    /// act, null when it must be denied (unknown subject, disabled
    /// user, inactive tenant, or stale/missing version — a legacy
    /// grant with no version claim can never pass).
    /// Directory failures propagate: a dead authority store denies
    /// by error rather than minting on faith.
    public static async Task<LolaUser?> ValidateAsync(
        ClaimsPrincipal principal,
        IUserDirectory users,
        ITenantRegistry tenants,
        CancellationToken ct)
    {
        var sub = principal.FindFirst(OpenIddictConstants.Claims.Subject)?.Value;
        var versionText = principal.FindFirst(VersionClaim)?.Value;
        if (sub is null || !long.TryParse(versionText, out var version))
            return null;

        var user = await users.FindByIdAsync(sub, ct);
        if (user is null || user.Status != UserStatus.Active
            || user.AuthVersion != version)
            return null;

        var tenant = await tenants.FindByIdAsync(user.TenantId, ct);
        return tenant?.Status == "active" ? user : null;
    }
}
