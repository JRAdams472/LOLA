using Lola.Application.Identity;

namespace Lola.Api.Invites;

/// Dev delivery seam (ADR-0007): no mailer exists yet, so "delivery"
/// is returning the link — the endpoint surfaces it to the officer,
/// who hands it to the borrower. The real impl swaps in behind the
/// same port without touching handlers (MPE-87).
public sealed class DevInviteDelivery(IConfiguration config) : IInviteDelivery
{
    public Task<Uri> DeliverAsync(string email, string tenantId, string token, CancellationToken ct = default)
    {
        var baseUrl = config["PORTAL_BASE_URL"]?.TrimEnd('/') ?? "http://localhost:3300";
        return Task.FromResult(new Uri($"{baseUrl}/invites/accept?token={Uri.EscapeDataString(token)}"));
    }
}
