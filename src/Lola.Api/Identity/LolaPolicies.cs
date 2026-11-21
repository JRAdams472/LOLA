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

    /// TenantAdmin + Service — tenant lifecycle (provision + status).
    public const string Provision = "tenant-provision";

    /// TenantAdmin + LoanOfficer — staff mutations inside a tenant
    /// (borrower invites).
    public const string TenantStaff = "tenant-staff";

    /// All six roles — the intake surface. Borrower reach is
    /// structurally own-draft-only and partner reach linked-only via
    /// handler guards (MPE-110, M19 row scope).
    public const string Applications = "applications";

    /// Broker + Realtor — the M19 TPO partner surface. Partners are
    /// NEVER in TenantStaff/AdminRead/RevealPii: their only reach is
    /// link-scoped application data through this policy's routes.
    public const string PartnerAccess = "partner-access";

    /// TenantAdmin + LoanOfficer — the audited PII reveal (MPE-117,
    /// invariant 8). Borrowers and service clients can never unmask.
    public const string RevealPii = "reveal-pii";

    /// Designated global clients only — the platform-operator surface
    /// (MPE-91/MPE-271): global tenant-registry actions (list/create/
    /// erase, fleet jobs) require the dedicated platform authority
    /// claim; tenant-bound Service principals are denied.
    public const string PlatformOps = "platform-ops";

    public static void Register(AuthorizationOptions options)
    {
        options.AddPolicy(TenantAdmin, p => p.RequireAssertion(
            ctx => HasRole(ctx.User, Domain.Identity.LolaRole.TenantAdmin)));
        options.AddPolicy(AdminRead, p => p.RequireAssertion(
            ctx => HasRole(ctx.User,
                Domain.Identity.LolaRole.TenantAdmin,
                Domain.Identity.LolaRole.LoanOfficer,
                Domain.Identity.LolaRole.Service)));
        options.AddPolicy(Provision, p => p.RequireAssertion(
            ctx => HasRole(ctx.User,
                Domain.Identity.LolaRole.TenantAdmin,
                Domain.Identity.LolaRole.Service)));
        options.AddPolicy(TenantStaff, p => p.RequireAssertion(
            ctx => HasRole(ctx.User,
                Domain.Identity.LolaRole.TenantAdmin,
                Domain.Identity.LolaRole.LoanOfficer)));
        options.AddPolicy(Applications, p => p.RequireAssertion(
            ctx => HasRole(ctx.User,
                Domain.Identity.LolaRole.TenantAdmin,
                Domain.Identity.LolaRole.LoanOfficer,
                Domain.Identity.LolaRole.Borrower,
                Domain.Identity.LolaRole.Service,
                Domain.Identity.LolaRole.Broker,
                Domain.Identity.LolaRole.Realtor)));
        options.AddPolicy(PartnerAccess, p => p.RequireAssertion(
            ctx => HasRole(ctx.User,
                Domain.Identity.LolaRole.Broker,
                Domain.Identity.LolaRole.Realtor)));
        options.AddPolicy(RevealPii, p => p.RequireAssertion(
            ctx => HasRole(ctx.User,
                Domain.Identity.LolaRole.TenantAdmin,
                Domain.Identity.LolaRole.LoanOfficer)));
        options.AddPolicy(PlatformOps, p => p.RequireAssertion(
            ctx => PlatformAuthority.IsPlatformPrincipal(ctx.User)));
    }

    /// Row-scope discriminant fed to application-bound commands (M19).
    /// Widest scope wins for multi-role principals: a staff role
    /// outranks a partner link, a partner link outranks borrower-owns.
    /// Policy admission already guarantees one of these claims, so the
    /// trailing default is unreachable in practice.
    public static Application.Applications.ActorScope ScopeFor(ClaimsPrincipal user)
    {
        if (HasRole(user,
                Domain.Identity.LolaRole.TenantAdmin,
                Domain.Identity.LolaRole.LoanOfficer,
                Domain.Identity.LolaRole.Service))
            return Application.Applications.ActorScope.Staff;
        if (HasRole(user,
                Domain.Identity.LolaRole.Broker,
                Domain.Identity.LolaRole.Realtor))
            return Application.Applications.ActorScope.Partner;
        if (HasRole(user, Domain.Identity.LolaRole.Borrower))
            return Application.Applications.ActorScope.Borrower;
        return Application.Applications.ActorScope.Staff;
    }

    public static bool HasRole(ClaimsPrincipal user, params Domain.Identity.LolaRole[] roles)
        => HasRole(user, roles.Select(r => r.ToString()).ToArray());

    public static bool HasRole(ClaimsPrincipal user, params string[] roles)
    {
        var wanted = roles.ToHashSet();
        return user.Claims.Any(c =>
            (c.Type == "role" || c.Type == ClaimTypes.Role) && wanted.Contains(c.Value));
    }
}
