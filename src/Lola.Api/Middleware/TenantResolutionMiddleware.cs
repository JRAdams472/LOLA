using System.Security.Claims;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Lola.Api.Middleware;

/// Resolves the `tid` claim to an active registry tenant once per
/// request and binds TenantContext + CipherContext + VisibilityContext
/// for everything downstream (MPE-86). Runs after authentication:
/// unauthenticated requests and tid-less principals (e.g. the global
/// service client) pass through — tenant-scoped endpoints 403 on a
/// missing scope themselves. Unknown/inactive/mismatched tenants are
/// refused with 403 problem details; no code path can name another
/// tenant's file (ADR-0003).
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext ctx, ITenantRegistry registry, IRoleVisibilityMap visibility)
    {
        var tid = ctx.User.Identity?.IsAuthenticated == true
            ? ctx.User.FindFirst("tid")?.Value
            : null;

        if (tid is not null)
        {
            var tenant = await registry.FindByIdAsync(tid, ctx.RequestAborted);
            if (tenant is null || !string.Equals(tenant.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "tenant is not active or does not exist",
                    Type = "https://httpstatuses.com/403"
                }, options: null, contentType: "application/problem+json");
                return;
            }

            var roles = ctx.User.Claims
                .Where(c => c.Type is "role" or ClaimTypes.Role)
                .Select(c => Enum.TryParse<LolaRole>(c.Value, out var r) ? r : (LolaRole?)null)
                .Where(r => r is not null).Select(r => r!.Value).ToArray();
            var visible = await visibility.VisibleClassesAsync(roles, ctx.RequestAborted);

            using var _ = TenantContext.Use(TenantScope.From(tenant));
            using var __ = CipherContext.Use(tenant.Id);
            using var ___ = VisibilityContext.Use(visible);
            await next(ctx);
            return;
        }

        await next(ctx);
    }
}
