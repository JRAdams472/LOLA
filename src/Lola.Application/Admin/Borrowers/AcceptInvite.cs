using System.Security.Cryptography;
using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.Application.Admin.Borrowers;

public sealed record AcceptInviteCommand(string Token, string DisplayName, string Password);

public sealed record AcceptInviteResult(string UserId, string TenantId, string Status);

/// Public set-password accept (MPE-87): the token IS the tenant
/// binding — no tenant context exists yet, and none is required
/// beyond the invite row. Single-use and expiry are enforced;
/// accepting grants the Borrower role via the tenant's `borrowers`
/// group (roles live on groups, ADR-0005).
public static class AcceptInviteHandler
{
    public static async Task<AcceptInviteResult> Handle(
        AcceptInviteCommand c, IInviteDirectory invites, IUserDirectory users,
        IGroupDirectory groups, IPasswordHasher hasher, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Token) || string.IsNullOrWhiteSpace(c.Password))
            throw DomainException.BadRequest("token and password are required");

        var hash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(c.Token))).ToLowerInvariant();
        var invite = await invites.FindByTokenHashAsync(hash, ct)
            ?? throw DomainException.BadRequest("invite is invalid");
        if (invite.UsedUtc is not null)
            throw DomainException.BadRequest("invite has already been used");
        if (DateTimeOffset.Parse(invite.ExpiresUtc) < DateTimeOffset.UtcNow)
            throw DomainException.BadRequest("invite has expired");
        if (await users.FindByEmailInTenantAsync(invite.TenantId, invite.Email, ct) is not null)
            throw DomainException.BadRequest("a user with this email already exists");

        var user = new LolaUser(
            $"u-{Guid.NewGuid():N}", invite.TenantId, invite.Email,
            c.DisplayName, hasher.Hash(c.Password), UserStatus.Active,
            MfaEnabled: false, DateTimeOffset.UtcNow.ToString("O"));
        await users.InsertAsync(user, ct);

        var borrowers = (await groups.ListByTenantAsync(invite.TenantId, ct))
            .FirstOrDefault(g => g.Name == "borrowers");
        if (borrowers is null)
        {
            borrowers = new LolaGroup($"g-{Guid.NewGuid():N}", invite.TenantId,
                "borrowers", "Borrower portal users", [LolaRole.Borrower]);
            await groups.InsertAsync(borrowers, ct);
        }
        await groups.AddMemberAsync(borrowers.Id, user.Id, ct);

        // Single-use last: if a concurrent accept consumed the invite,
        // this loses the race and the caller gets an honest 400.
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (!await invites.MarkUsedAsync(invite.Id, now, ct))
            throw DomainException.BadRequest("invite has already been used");

        return new AcceptInviteResult(user.Id, invite.TenantId, "active");
    }
}
