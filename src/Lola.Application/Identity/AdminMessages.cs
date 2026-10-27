using Lola.Domain.Classification;
using Lola.Domain.Identity;

namespace Lola.Application.Identity;

/// Admin-plane messages (MPE-86). Tenant-scoped operations carry the
/// tenant id in the message AND are re-checked against TenantContext
/// inside the handler — the message arg can never widen scope.

public sealed record GetTenantsQuery;

public sealed record GetTenantUsersQuery(string TenantId);

public sealed record ListGroupsQuery(string TenantId);

public sealed record CreateGroupCommand(
    string TenantId, string Name, string? Description, IReadOnlyList<LolaRole> Roles);

public sealed record UpdateGroupCommand(
    string TenantId, string GroupId, string? Name, string? Description,
    IReadOnlyList<LolaRole>? Roles);

public sealed record DeleteGroupCommand(string TenantId, string GroupId);

public sealed record AddUserToGroupCommand(string TenantId, string GroupId, string UserId);

public sealed record RemoveUserFromGroupCommand(string TenantId, string GroupId, string UserId);

public sealed record CreateOfficerUserCommand(
    string TenantId, string Email, string DisplayName, string? Password);

// --- DTOs ---------------------------------------------------------------

public sealed record TenantAdminDto(
    string Id, string Slug, string DisplayName, string Status, string CreatedUtc);

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

public sealed record GroupDto(
    string Id, string Name, string? Description,
    IReadOnlyList<string> Roles, int MemberCount);

public sealed record OfficerUserResult(string Id, string Email, string Status);
