using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Lola.Api.Identity;

/// Role→policy wiring (ADR-0005). Roles arrive as `role` claims in
/// tokens; both the OIDC short form and the full URI form are accepted
/// so policies hold regardless of the claim-mapping layer.
public static class LolaPolicies
{
    /// TenantAdmin only — mutations and the global registry view.
    public const string TenantAdmin = "tenant-admin";

    /// TenantAdmin + LoanOfficer + Service — read-only admin views;
    /// the role→visibility map still masks per class (ADR-0008).
    public const string AdminRead = "admin-read";

    public static void Register(AuthorizationOptions options)
    {
        options.AddPolicy(TenantAdmin, p => p.RequireAssertion(
            ctx => HasRole(ctx.User, Domain.Identity.LolaRole.TenantAdmin)));
        options.AddPolicy(AdminRead, p => p.RequireAssertion(
            ctx => HasRole(ctx.User,
                Domain.Identity.LolaRole.TenantAdmin,
                Domain.Identity.LolaRole.LoanOfficer,
                Domain.Identity.LolaRole.Service)));
    }

    public static bool HasRole(ClaimsPrincipal user, params Domain.Identity.LolaRole[] roles)
    {
        var wanted = roles.Select(r => r.ToString()).ToHashSet();
        return user.Claims.Any(c =>
            (c.Type == "role" || c.Type == ClaimTypes.Role) && wanted.Contains(c.Value));
    }
}
