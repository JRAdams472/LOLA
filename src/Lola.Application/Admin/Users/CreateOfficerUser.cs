using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.Application.Admin.Users;

public sealed record CreateOfficerUserCommand(
    string TenantId, string Email, string DisplayName, string? Password);

public sealed record OfficerUserResult(string Id, string Email, string Status);

/// Officer/staff user creation (MPE-86). A missing password leaves the
/// account `invited` — the Phase-4 invite flow completes activation.
public static class CreateOfficerUserHandler
{
    public static async Task<OfficerUserResult> Handle(
        CreateOfficerUserCommand c, IUserDirectory users,
        IPasswordHasher hasher, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (string.IsNullOrWhiteSpace(c.Email))
            throw DomainException.BadRequest("email is required");
        var user = new LolaUser(
            $"u-{Guid.NewGuid():N}", c.TenantId, c.Email, c.DisplayName,
            string.IsNullOrEmpty(c.Password) ? null : hasher.Hash(c.Password),
            string.IsNullOrEmpty(c.Password) ? UserStatus.Invited : UserStatus.Active,
            MfaEnabled: false, DateTimeOffset.UtcNow.ToString("O"));
        await users.InsertAsync(user, ct);
        return new OfficerUserResult(user.Id, user.Email,
            user.Status.ToString().ToLowerInvariant());
    }
}
