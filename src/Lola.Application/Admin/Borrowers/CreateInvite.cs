using System.Security.Cryptography;
using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.Application.Admin.Borrowers;

public sealed record CreateInviteCommand(string TenantId, string Email, string CreatedBy);

public sealed record InviteCreatedResult(string InviteId, Uri InviteUrl, string ExpiresUtc);

/// Borrower invite (MPE-87): 256-bit token, only its SHA-256 hash is
/// stored, 72h TTL. The delivery seam returns the link the response
/// surfaces once — dev echoes it, a real impl emails it.
public static class CreateInviteHandler
{
    public static async Task<InviteCreatedResult> Handle(
        CreateInviteCommand c, IInviteDirectory invites,
        IUserDirectory users, IInviteDelivery delivery, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (string.IsNullOrWhiteSpace(c.Email))
            throw DomainException.BadRequest("email is required");
        if (await users.FindByEmailInTenantAsync(c.TenantId, c.Email, ct) is not null)
            throw DomainException.BadRequest("a user with this email already exists");

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(token))).ToLowerInvariant();
        var expires = DateTimeOffset.UtcNow.AddHours(72).ToString("O");
        var invite = new InviteRecord(
            $"i-{Guid.NewGuid():N}", c.TenantId, c.Email, LolaRole.Borrower,
            hash, expires, null, c.CreatedBy);
        await invites.InsertAsync(invite, ct);

        var link = await delivery.DeliverAsync(c.Email, c.TenantId, token, ct);
        return new InviteCreatedResult(invite.Id, link, expires);
    }
}
