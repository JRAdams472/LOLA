using Lola.Application.Identity;
using Lola.Domain.Classification;

namespace Lola.Application.Admin.Users;

public sealed record GetTenantUsersQuery(string TenantId, string ActorId);

/// Admin user view. Email/name are Contact/Name-class PII; the MFA
/// secret reference is Account-class credential material — all masked
/// per role_visibility (TenantAdmin sees all; LoanOfficer sees
/// Contact/Name but not Account; Borrower/Service see nothing).
public sealed record AdminUserDto(
    string Id,
    [property: PiiClass(PiiClass.Contact)] string Email,
    [property: PiiClass(PiiClass.Name)] string DisplayName,
    string Status,
    IReadOnlyList<string> Roles,
    [property: PiiClass(PiiClass.Account)] string? MfaSecretRef);

/// Tenant user list — shared by REST and the GraphQL resolver (MPE-86).
public static class GetTenantUsersHandler
{
    public static async Task<IReadOnlyList<AdminUserDto>> Handle(
        GetTenantUsersQuery q, IUserDirectory users,
        Audit.IControlPlaneAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var result = new List<AdminUserDto>();
        foreach (var u in await users.ListByTenantAsync(q.TenantId, ct))
        {
            var roles = await users.RolesForUserAsync(u.Id, ct);
            result.Add(new AdminUserDto(u.Id, u.Email, u.DisplayName,
                u.Status.ToString().ToLowerInvariant(),
                roles.Select(r => r.ToString()).ToList(), u.MfaSecretRef));
        }
        // Control-plane audit (MPE-117): identity PII reads land in the
        // control-plane log — classes only, never values (invariant 3).
        await audit.WriteAsync(q.ActorId, "read", "user", null,
            ["Contact", "Name", "Account"], "tenant user list", ct);
        return result;
    }
}
