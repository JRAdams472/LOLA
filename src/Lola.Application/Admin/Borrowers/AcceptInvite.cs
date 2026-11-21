using System.Security.Cryptography;
using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.Application.Admin.Borrowers;

public sealed record AcceptInviteCommand(string Token, string DisplayName, string Password);

public sealed record AcceptInviteResult(string UserId, string TenantId, string Status);

/// Public set-password accept (MPE-87; partner roles M19): the token
/// IS the tenant binding — no tenant context exists yet, and none is
/// required beyond the invite row. Single-use and expiry are
/// enforced; accepting grants the invite's role via the tenant's
/// matching public group — `borrowers`/`brokers`/`realtors` — which
/// is lazily created on first use (roles live on groups, ADR-0005).
public static class AcceptInviteHandler
{
    public static async Task<AcceptInviteResult> Handle(
        AcceptInviteCommand c, IInviteDirectory invites, IUserDirectory users,
        IGroupDirectory groups, IPasswordHasher hasher, CancellationToken ct)
    {
        // Cheap boundary validation before any lookup — a malformed
        // request fails identically whether or not the token is real.
        if (string.IsNullOrWhiteSpace(c.Token))
            throw DomainException.BadRequest("token is required");
        var displayName = InputValidation.RequireDisplayName(c.DisplayName);
        InputValidation.RequirePassword(c.Password);

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

        // Role comes from the invite, not the caller — group name and
        // granted role derive together (M19 partner roles). Staff-role
        // invites are never issuable, but fail closed here too.
        var (groupName, groupDesc, role) = invite.Role switch
        {
            LolaRole.Borrower => ("borrowers", "Borrower portal users",
                LolaRole.Borrower),
            LolaRole.Broker => ("brokers", "Broker partners (TPO)",
                LolaRole.Broker),
            LolaRole.Realtor => ("realtors", "Realtor partners (TPO)",
                LolaRole.Realtor),
            _ => throw DomainException.BadRequest(
                "invite role is not acceptable through this flow")
        };

        var user = new LolaUser(
            $"u-{Guid.NewGuid():N}", invite.TenantId, invite.Email,
            displayName, hasher.Hash(c.Password), UserStatus.Active,
            MfaEnabled: false, DateTimeOffset.UtcNow.ToString("O"),
            OrgName: invite.OrgName);
        await users.InsertAsync(user, ct);

        var grp = (await groups.ListByTenantAsync(invite.TenantId, ct))
            .FirstOrDefault(g => g.Name == groupName);
        if (grp is null)
        {
            grp = new LolaGroup($"g-{Guid.NewGuid():N}", invite.TenantId,
                groupName, groupDesc, [role]);
            await groups.InsertAsync(grp, ct);
        }
        await groups.AddMemberAsync(grp.Id, user.Id, ct);

        // Single-use last: if a concurrent accept consumed the invite,
        // this loses the race and the caller gets an honest 400.
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (!await invites.MarkUsedAsync(invite.Id, now, ct))
            throw DomainException.BadRequest("invite has already been used");

        return new AcceptInviteResult(user.Id, invite.TenantId, "active");
    }
}
