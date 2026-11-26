using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Application.Identity;

namespace Lola.Application.Compliance;

/// POST …/respa/servicing/notices/{noticeId}/sent (M23 P2) — marks a
/// pending notice sent. The transition appends a notice-event row and
/// a hash-chained loan event carrying the timely/late determination —
/// `ServicingTransfer.SentLate` is the same domain predicate the
/// engine tests prove.
public sealed record MarkNoticeSentCommand(
    string TenantId, string AppId, string NoticeId, string ActorId,
    ActorScope Scope);

public static class MarkNoticeSentHandler
{
    public static async Task<ServicingNoticeDto> Handle(
        MarkNoticeSentCommand c, IApplicationRepository apps,
        IRespaRepository respa, ILoanEventLog events,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "notice transitions are a staff action");
        await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, ActorScope.Staff, ct);

        var notice = await respa.GetNoticeAsync(c.NoticeId, ct);
        if (notice is null || notice.ApplicationId != c.AppId)
            throw Errors.DomainException.NotFound("notice not found");
        if (notice.Status != "pending")
            throw Errors.DomainException.Conflict(
                $"notice is {notice.Status} — only pending notices can be marked sent");

        var sentUtc = await respa.TransitionNoticeAsync(
            c.NoticeId, "sent", c.ActorId, ct);

        var sendBy = DateOnly.Parse(notice.SendBy);
        var sentOn = DateOnly.Parse(sentUtc[..10]);
        var late = Domain.Compliance.ServicingTransfer.SentLate(
            sendBy, sentOn);

        await events.WriteAsync(new LoanEventDraft(c.AppId,
            LoanEventKind.ServicingNotice, c.ActorId,
            "servicing_notice", c.NoticeId, "compliance", null,
            "pending", late ? "sent:late" : "sent:timely"), ct);

        var updated = await respa.GetNoticeAsync(c.NoticeId, ct);
        return await GetApplicationRespaHandler.ToDto(respa, updated!, ct);
    }
}
