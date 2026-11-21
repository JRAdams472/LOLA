using System.Security.Cryptography;
using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.Application.Admin.Borrowers;

/// `Role` selects the invite's grant — public roles only (the accept
/// flow can never mint staff); `OrgName` carries the M19 TPO
/// organization attribution onto the created user.
public sealed record CreateInviteCommand(
    string TenantId, string Email, string CreatedBy,
    LolaRole Role = LolaRole.Borrower, string? OrgName = null);

public sealed record InviteCreatedResult(string InviteId, Uri InviteUrl, string ExpiresUtc);

/// Borrower/partner invite (MPE-87; M19): 256-bit token, only its
/// SHA-256 hash is stored, 72h TTL. The delivery seam returns the
/// link the response surfaces once — dev echoes it, a real impl
/// emails it.
public static class CreateInviteHandler
{
    private static readonly HashSet<LolaRole> Invitable =
        [LolaRole.Borrower, LolaRole.Broker, LolaRole.Realtor];

    public static async Task<InviteCreatedResult> Handle(
        CreateInviteCommand c, IInviteDirectory invites,
        IUserDirectory users, IInviteDelivery delivery, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (!Invitable.Contains(c.Role))
            throw DomainException.BadRequest(
                "invites can only grant Borrower, Broker, or Realtor");
        var email = InputValidation.RequireEmail(c.Email);
        if (await users.FindByEmailInTenantAsync(c.TenantId, email, ct) is not null)
            throw DomainException.BadRequest("a user with this email already exists");

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(token))).ToLowerInvariant();
        var expires = DateTimeOffset.UtcNow.AddHours(72).ToString("O");
        var invite = new InviteRecord(
            $"i-{Guid.NewGuid():N}", c.TenantId, email, c.Role,
            hash, expires, null, c.CreatedBy, c.OrgName);
        await invites.InsertAsync(invite, ct);

        var link = await delivery.DeliverAsync(email, c.TenantId, token, ct);
        return new InviteCreatedResult(invite.Id, link, expires);
    }
}
