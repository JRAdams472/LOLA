using Lola.Api.Identity;
using Lola.Application.Admin.Groups;
using Lola.Application.Admin.Tenants;
using Lola.Application.Admin.Users;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.Domain.Identity;
using Wolverine;

namespace Lola.Api.Admin;

/// Tenant-admin surface (MPE-86). Every tenant-scoped route verifies
/// the path tenant against the resolved TenantContext — a mismatched
/// {id} is 403, and the handlers re-check so GraphQL is covered too.
/// Read endpoints admit TenantAdmin/LoanOfficer/Service so the
/// role→visibility masking is observable per map; mutations are
/// TenantAdmin-only.
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/admin");

        admin.MapGet("/tenants", async (IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeMaskedAsync<IReadOnlyList<TenantAdminDto>>(
                new GetTenantsQuery(), ct)))
            .RequireAuthorization(LolaPolicies.TenantAdmin);

        admin.MapGet("/tenants/{id}/users", async (
                string id, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Ok(
                await bus.InvokeMaskedAsync<IReadOnlyList<AdminUserDto>>(
                    new GetTenantUsersQuery(id), ct))))
            .RequireAuthorization(LolaPolicies.AdminRead);

        admin.MapGet("/tenants/{id}/groups", async (
                string id, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Ok(
                await bus.InvokeMaskedAsync<IReadOnlyList<GroupDto>>(
                    new ListGroupsQuery(id), ct))))
            .RequireAuthorization(LolaPolicies.AdminRead);

        admin.MapPost("/tenants/{id}/users", async (
                string id, CreateUserRequest body, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Created(
                $"/api/admin/tenants/{id}/users",
                await bus.InvokeMaskedAsync<OfficerUserResult>(new CreateOfficerUserCommand(
                    id, body.Email, body.DisplayName, body.Password), ct))))
            .RequireAuthorization(LolaPolicies.TenantAdmin);

        admin.MapPost("/tenants/{id}/groups", async (
                string id, GroupRequest body, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Created(
                $"/api/admin/tenants/{id}/groups",
                await bus.InvokeMaskedAsync<GroupDto>(new CreateGroupCommand(
                    id, body.Name, body.Description, ParseRoles(body.Roles)), ct))))
            .RequireAuthorization(LolaPolicies.TenantAdmin);

        admin.MapPut("/tenants/{id}/groups/{gid}", async (
                string id, string gid, GroupRequest body, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Ok(
                await bus.InvokeMaskedAsync<GroupDto>(new UpdateGroupCommand(
                    id, gid, body.Name, body.Description,
                    body.Roles is null ? null : ParseRoles(body.Roles)), ct))))
            .RequireAuthorization(LolaPolicies.TenantAdmin);

        admin.MapDelete("/tenants/{id}/groups/{gid}", async (
                string id, string gid, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Ok(
                await bus.InvokeMaskedAsync<GroupDto>(new DeleteGroupCommand(id, gid), ct))))
            .RequireAuthorization(LolaPolicies.TenantAdmin);

        admin.MapPost("/tenants/{id}/groups/{gid}/members", async (
                string id, string gid, MemberRequest body, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Ok(
                await bus.InvokeMaskedAsync<GroupDto>(new AddUserToGroupCommand(
                    id, gid, body.UserId), ct))))
            .RequireAuthorization(LolaPolicies.TenantAdmin);

        admin.MapDelete("/tenants/{id}/groups/{gid}/members/{uid}", async (
                string id, string gid, string uid, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Ok(
                await bus.InvokeMaskedAsync<GroupDto>(new RemoveUserFromGroupCommand(
                    id, gid, uid), ct))))
            .RequireAuthorization(LolaPolicies.TenantAdmin);
    }

    /// Runs the work only when the resolved tenant matches the path —
    /// null scope (tid-less service token) or mismatch is 403.
    private static async Task<IResult> Guarded(string tenantId, Func<Task<IResult>> work)
    {
        if (TenantContext.Current?.TenantId != tenantId)
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "request tenant does not match the resolved tenant");
        return await work();
    }

    private static IReadOnlyList<LolaRole> ParseRoles(IReadOnlyList<string>? roles)
        => (roles ?? [])
            .Select(r => Enum.TryParse<LolaRole>(r, ignoreCase: true, out var p)
                ? p
                : throw new ArgumentException($"unknown role '{r}'"))
            .ToList();

    public sealed record CreateUserRequest(
        string Email, string DisplayName, string? Password);

    public sealed record GroupRequest(
        string Name, string? Description, IReadOnlyList<string>? Roles);

    public sealed record MemberRequest(string UserId);
}
