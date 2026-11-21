using Lola.Application.Admin;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Events;
using Lola.Application.Identity;

namespace Lola.Application.Applications;

/// DELETE …/applications/{id}/partners/{userId} (M19, TenantStaff) —
/// revokes a partner's link: `revoked_utc` stamps the row (soft
/// delete keeps provenance), the scope guard treats it as absent, and
/// every subsequent partner request on the app is 403.
public sealed record UnlinkPartnerCommand(
    string TenantId, string AppId, string PartnerUserId, string ActorId);

public static class UnlinkPartnerHandler
{
    public static async Task<bool> Handle(
        UnlinkPartnerCommand c, IApplicationRepository apps,
        IApplicationPartnerRepository links,
        ITenantAuditLog audit, ILoanEventLog events, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, ActorScope.Staff, ct);
        if (!await links.RevokeAsync(c.AppId, c.PartnerUserId,
                DateTimeOffset.UtcNow.ToString("O"), ct))
            throw DomainException.NotFound("no active partner link");
        await audit.WriteAsync(c.ActorId, "partner_revoked",
            "application", c.AppId, ["application_partners"],
            $"revoked partner link {c.PartnerUserId}", ct);
        await events.WriteAsync(new LoanEventDraft(c.AppId,
            LoanEventKind.PartnerRevoked, c.ActorId,
            "application_partner", c.PartnerUserId,
            AfterVal: "revoked"), ct);
        return true;
    }
}
