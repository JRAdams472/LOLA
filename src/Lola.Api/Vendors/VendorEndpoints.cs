using System.Security.Claims;
using Lola.Api.Identity;
using Lola.Application.Tenancy;
using Lola.Application.Vendors;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Http;
using Wolverine;

namespace Lola.Api.Vendors;

/// Vendor order surface (MPE-227) — `/api/tenants/{id}/applications/
/// {appId}/vendor-orders`. Staff-only semantics live inside the
/// handlers (borrower tokens get 403 there — the Applications policy
/// admits the role so the guard is the denial point, same as fees).
public static class VendorEndpoints
{
    public static void MapVendorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(
                "/api/tenants/{id}/applications/{appId}/vendor-orders")
            .RequireAuthorization(LolaPolicies.Applications)
            .RequireRateLimiting("intake");

        group.MapPost("", async (string id, string appId,
                VendorOrderRequestBody? body, ClaimsPrincipal user,
                IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await
                bus.InvokeAsync<VendorOrderAccepted>(
                    new PlaceVendorOrderCommand(id, appId,
                        Actor(user), LolaPolicies.ScopeFor(user),
                        body?.Kind ?? ""), ct)));

        group.MapGet("", async (string id, string appId,
                ClaimsPrincipal user, IMessageBus bus,
                CancellationToken ct) => await
            Guarded(id, async () => await
                bus.InvokeAsync<IReadOnlyList<VendorOrderDto>>(
                    new ListVendorOrdersQuery(id, appId,
                        Actor(user), LolaPolicies.ScopeFor(user)), ct)));

        group.MapGet("/{orderId}", async (string id, string appId,
                string orderId, ClaimsPrincipal user,
                IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await
                bus.InvokeAsync<VendorOrderDto?>(
                    new GetVendorOrderQuery(id, appId, orderId,
                        Actor(user), LolaPolicies.ScopeFor(user)), ct)));
    }

    private static async Task<IResult> Guarded(string tenantId,
        Func<Task<object?>> work)
    {
        if (TenantContext.Current?.TenantId != tenantId)
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "request tenant does not match the resolved tenant");
        var result = await work();
        return result switch
        {
            VendorOrderAccepted accepted => Results.Accepted(null, accepted),
            null => Results.NotFound(),
            _ => Results.Ok(result)
        };
    }

    private static string Actor(ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value ?? "unknown";

    public sealed record VendorOrderRequestBody(string? Kind);
}
