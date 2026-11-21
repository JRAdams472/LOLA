using Lola.Application.Admin;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Events;
using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.Application.Applications;

/// PUT …/applications/{id}/partners/{userId} (M19, TenantStaff) —
/// links an external partner to one application. The partner's role
/// derives from their actual group roles (never caller-supplied), so
/// a non-partner user is a 400, not a misattributed link. Re-linking
/// a revoked partner re-activates the row.
public sealed record LinkPartnerCommand(
    string TenantId, string AppId, string PartnerUserId, string ActorId);

public sealed record PartnerLinkDto(
    string PartnerUserId, string Role, string LinkedBy,
    string LinkedUtc, string? RevokedUtc);

public static class LinkPartnerHandler
{
    public static async Task<PartnerLinkDto> Handle(
        LinkPartnerCommand c, IApplicationRepository apps,
        IApplicationPartnerRepository links, IUserDirectory users,
        ITenantAuditLog audit, ILoanEventLog events, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, ActorScope.Staff, ct);

        var partner = await users.FindByIdAsync(c.PartnerUserId, ct);
        if (partner is null || partner.TenantId != c.TenantId)
            throw DomainException.BadRequest(
                "partner user does not exist in this tenant");
        var partnerRole = (await users.RolesForUserAsync(partner.Id, ct))
            .FirstOrDefault(r => r is LolaRole.Broker or LolaRole.Realtor);
        if (partnerRole is not (LolaRole.Broker or LolaRole.Realtor))
            throw DomainException.BadRequest(
                "user holds no partner role (Broker/Realtor)");

        var link = new PartnerLink(c.AppId, c.PartnerUserId,
            partnerRole.ToString(), c.ActorId,
            DateTimeOffset.UtcNow.ToString("O"), null);
        await links.LinkAsync(link, ct);
        await audit.WriteAsync(c.ActorId, "partner_linked",
            "application", c.AppId, ["application_partners"],
            $"linked {link.PartnerRole} partner {c.PartnerUserId}", ct);
        await events.WriteAsync(new LoanEventDraft(c.AppId,
            LoanEventKind.PartnerLinked, c.ActorId,
            "application_partner", c.PartnerUserId,
            FieldPath: $"partners.{link.PartnerRole.ToLowerInvariant()}",
            AfterVal: "linked"), ct);
        return new PartnerLinkDto(link.PartnerUserId, link.PartnerRole,
            link.LinkedBy, link.LinkedUtc, link.RevokedUtc);
    }
}
