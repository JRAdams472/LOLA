using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.Application.Admin.Users;

public sealed record CreateOfficerUserCommand(
    string TenantId, string Email, string DisplayName, string? Password,
    string ActorId);

public sealed record OfficerUserResult(string Id, string Email, string Status);

/// Officer/staff user creation (MPE-86). A missing password leaves the
/// account `invited` — the Phase-4 invite flow completes activation.
public static class CreateOfficerUserHandler
{
    public static async Task<OfficerUserResult> Handle(
        CreateOfficerUserCommand c, IUserDirectory users,
        IPasswordHasher hasher, Audit.IControlPlaneAuditLog audit,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var email = InputValidation.RequireEmail(c.Email);
        var displayName = InputValidation.RequireDisplayName(c.DisplayName);
        // Null/empty password = invited account (activated via the
        // invite flow); a provided password must meet policy.
        if (!string.IsNullOrEmpty(c.Password))
            InputValidation.RequirePassword(c.Password);
        var user = new LolaUser(
            $"u-{Guid.NewGuid():N}", c.TenantId, email, displayName,
            string.IsNullOrEmpty(c.Password) ? null : hasher.Hash(c.Password),
            string.IsNullOrEmpty(c.Password) ? UserStatus.Invited : UserStatus.Active,
            MfaEnabled: false, DateTimeOffset.UtcNow.ToString("O"));
        await users.InsertAsync(user, ct);
        // Control-plane audit (MPE-117): a PII write on the identity
        // surface — classes only, never values.
        await audit.WriteAsync(c.ActorId, "write", "user", user.Id,
            ["Contact", "Name"], "officer user create", ct);
        return new OfficerUserResult(user.Id, user.Email,
            user.Status.ToString().ToLowerInvariant());
    }
}
