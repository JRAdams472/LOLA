using System.Security.Claims;
using Lola.Api.Identity;
using Lola.Application.Admin.Borrowers;
using Lola.Application.Admin.Groups;
using Lola.Application.Admin.Tenants;
using Lola.Application.Admin.Users;
using Lola.Application.Identity;
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

        // Tenant lifecycle (MPE-87): create is unscoped by definition —
        // the tenant does not exist yet; status reads are own-tenant
        // for user tokens or any-tenant for Service.
        admin.MapPost("/tenants", async (
                ProvisionTenantRequest body, IMessageBus bus, CancellationToken ct) =>
            Results.Created($"/api/admin/tenants/{body.TenantId}",
                await bus.InvokeMaskedAsync<ProvisionTenantResult>(
                    new ProvisionTenantCommand(body.TenantId, body.Slug, body.DisplayName), ct)))
            .RequireAuthorization(LolaPolicies.Provision);

        // Fleet migration rollout (MPE-93) — unscoped operator surface:
        // rolls every active tenant file to the latest migration,
        // isolating failures per tenant.
        admin.MapPost("/migrations", async (IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeMaskedAsync<MigrationRunResult>(
                new RunMigrationsCommand(), ct)))
            .RequireAuthorization(LolaPolicies.Provision);

        // Encrypted snapshot lifecycle (MPE-95) — unscoped operator
        // surface: backups stay under the tenant's own file key, so the
        // Provision policy that already runs fleet jobs guards them.
        admin.MapPost("/tenants/{id}/backup", async (
                string id, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeMaskedAsync<TenantBackupInfo>(
                new BackupTenantCommand(id), ct)))
            .RequireAuthorization(LolaPolicies.Provision);

        admin.MapGet("/tenants/{id}/backups", async (
                string id, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeMaskedAsync<IReadOnlyList<TenantBackupInfo>>(
                new ListTenantBackupsQuery(id), ct)))
            .RequireAuthorization(LolaPolicies.Provision);

        admin.MapPost("/tenants/{id}/restore", async (
                string id, RestoreRequest body, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeMaskedAsync<TenantBackupInfo>(
                new RestoreTenantBackupCommand(id, body.Name), ct)))
            .RequireAuthorization(LolaPolicies.Provision);

        // Crypto-shred erasure (MPE-96) — keys die before files
        // (invariant 9); the handler guards retired/unknown tenants and
        // gates on the Reg B 25-month window (MPE-118): ?force=true&reason=
        // overrides with a control-plane retention_override audit event.
        admin.MapDelete("/tenants/{id}", async (
                string id, bool? force, string? reason, HttpContext ctx,
                IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeMaskedAsync<TenantErasureResult>(
                new EraseTenantCommand(id, force == true, reason,
                    ctx.User.FindFirst("sub")?.Value ?? "unknown"), ct)))
            .RequireAuthorization(LolaPolicies.Provision);

        admin.MapGet("/tenants/{id}", async (
                string id, HttpContext ctx, IMessageBus bus, CancellationToken ct) =>
            await GuardedStatus(id, ctx.User, async () => Results.Ok(
                await bus.InvokeMaskedAsync<TenantAdminDto>(new GetTenantQuery(id), ct))))
            .RequireAuthorization(LolaPolicies.AdminRead);

        // Borrower invite — link returned once via the delivery seam.
        admin.MapPost("/tenants/{id}/invites", async (
                string id, InviteRequest body, HttpContext ctx, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Created($"/api/admin/tenants/{id}",
                await bus.InvokeMaskedAsync<InviteCreatedResult>(new CreateInviteCommand(
                    id, body.Email, ctx.User.FindFirst("sub")?.Value ?? "unknown"), ct))))
            .RequireAuthorization(LolaPolicies.TenantStaff);

        admin.MapGet("/tenants/{id}/users", async (
                string id, HttpContext ctx, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Ok(
                await bus.InvokeMaskedAsync<IReadOnlyList<AdminUserDto>>(
                    new GetTenantUsersQuery(id,
                        ctx.User.FindFirst("sub")?.Value ?? "unknown"), ct))))
            .RequireAuthorization(LolaPolicies.AdminRead);

        admin.MapGet("/tenants/{id}/groups", async (
                string id, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Ok(
                await bus.InvokeMaskedAsync<IReadOnlyList<GroupDto>>(
                    new ListGroupsQuery(id), ct))))
            .RequireAuthorization(LolaPolicies.AdminRead);

        admin.MapPost("/tenants/{id}/users", async (
                string id, CreateUserRequest body, HttpContext ctx, IMessageBus bus, CancellationToken ct) =>
            await Guarded(id, async () => Results.Created(
                $"/api/admin/tenants/{id}/users",
                await bus.InvokeMaskedAsync<OfficerUserResult>(new CreateOfficerUserCommand(
                    id, body.Email, body.DisplayName, body.Password,
                    ctx.User.FindFirst("sub")?.Value ?? "unknown"), ct))))
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

    /// Tenant-status guard: a resolved tenant must match; a tid-less
    /// principal (the global service client) must be Service —
    /// user tokens always carry `tid`, so scope-less + non-Service
    /// never reaches here through the AdminRead policy anyway.
    private static async Task<IResult> GuardedStatus(
        string tenantId, ClaimsPrincipal user, Func<Task<IResult>> work)
    {
        var scope = TenantContext.Current;
        if (scope is not null)
            return scope.TenantId == tenantId
                ? await work()
                : Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "request tenant does not match the resolved tenant");
        return LolaPolicies.HasRole(user, Domain.Identity.LolaRole.Service)
            ? await work()
            : Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "tenant status requires a tenant or service scope");
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

    public sealed record ProvisionTenantRequest(string TenantId, string Slug, string DisplayName);

    public sealed record InviteRequest(string Email);

    public sealed record RestoreRequest(string Name);
}
