using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Application.Identity;

namespace Lola.Application.Compliance;

/// POST …/respa/servicing/notices/{noticeId}/received (M23 P2) —
/// marks a sent notice received (borrower acknowledgement of record).
/// Received-before-sent is an illegal transition — 409.
public sealed record MarkNoticeReceivedCommand(
    string TenantId, string AppId, string NoticeId, string ActorId,
    ActorScope Scope);

public static class MarkNoticeReceivedHandler
{
    public static async Task<ServicingNoticeDto> Handle(
        MarkNoticeReceivedCommand c, IApplicationRepository apps,
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
        if (notice.Status != "sent")
            throw Errors.DomainException.Conflict(
                $"notice is {notice.Status} — only sent notices can be marked received");

        await respa.TransitionNoticeAsync(
            c.NoticeId, "received", c.ActorId, ct);

        await events.WriteAsync(new LoanEventDraft(c.AppId,
            LoanEventKind.ServicingNotice, c.ActorId,
            "servicing_notice", c.NoticeId, "compliance", null,
            "sent", "received"), ct);

        var updated = await respa.GetNoticeAsync(c.NoticeId, ct);
        return await GetApplicationRespaHandler.ToDto(respa, updated!, ct);
    }
}
