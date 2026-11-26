using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Application.Identity;

namespace Lola.Application.Compliance;

/// GET …/applications/{id}/respa (M23 P2) — the servicing/escrow
/// posture: latest analysis + append-only lineage, the Section 8 AfBA
/// disclosure flag, and notices with computed deadlines evaluated
/// live. A pending notice past its send-by deadline is overdue — the
/// sweep opens ONE `servicing_notice` condition per notice, deduped
/// by linked entity (the M21 tolerance-cure discipline): a repeat
/// read never double-opens.
public sealed record GetApplicationRespaQuery(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class GetApplicationRespaHandler
{
    public static async Task<RespaReportDto> Handle(
        GetApplicationRespaQuery q, IApplicationRepository apps,
        IRespaRepository respa, Events.IConditionRepository conditions,
        ILoanEventLog events, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "respa posture is staff-facing");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, ActorScope.Staff, ct);

        var analyses = await respa.ListAnalysesAsync(q.AppId, ct);
        var analysisDtos = analyses
            .Select(r => RunEscrowAnalysisHandler.ToDto(r)).ToList();

        var notices = await respa.ListNoticesAsync(q.AppId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var noticeDtos = new List<ServicingNoticeDto>();
        foreach (var notice in notices)
        {
            var dto = await ToDto(respa, notice, ct, today);
            noticeDtos.Add(dto);
            if (dto.Overdue)
                await OpenOverdueConditionOnce(q.AppId, notice,
                    conditions, events, ct);
        }

        return new RespaReportDto(
            analysisDtos.Count == 0 ? null : analysisDtos[0],
            analysisDtos,
            RespaEvaluator.AfbaRequired(app.Doc),
            noticeDtos);
    }

    /// Dedupe by linked entity: the notice's condition is unique per
    /// notice id — an open one is never duplicated, a closed/waived
    /// one stays closed (staff disposition stands).
    private static async Task OpenOverdueConditionOnce(string appId,
        ServicingNoticeRecord notice,
        Events.IConditionRepository conditions,
        ILoanEventLog events, CancellationToken ct)
    {
        var open = (await conditions.ListByApplicationAsync(
                appId, Events.ConditionState.Open, ct))
            .FirstOrDefault(c =>
                c.Kind == Events.ConditionKind.ServicingNotice
                && c.LinkedEntityId == notice.NoticeId);
        if (open is not null) return;
        await conditions.OpenAsync(new Events.ConditionDraft(appId,
            Events.ConditionKind.ServicingNotice,
            $"Servicing notice overdue — {notice.NoticeKind}",
            $"send-by {notice.SendBy} passed with no sent record",
            Blocking: true,
            LinkedEntity: Events.ConditionLink.ServicingNotice,
            LinkedEntityId: notice.NoticeId), ct);
    }

    /// Wire shape for one notice: deadline, status, live statutory
    /// evaluation, and the append-only event lineage. Sent/received
    /// timestamps derive from the event rows — the notice row itself
    /// never mutates history.
    internal static async Task<ServicingNoticeDto> ToDto(
        IRespaRepository respa, ServicingNoticeRecord n,
        CancellationToken ct, DateOnly? today = null)
    {
        var evts = await respa.ListNoticeEventsAsync(n.NoticeId, ct);
        var sent = evts.LastOrDefault(e => e.Status == "sent");
        var received = evts.LastOrDefault(e => e.Status == "received");
        var now = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var sendBy = DateOnly.Parse(n.SendBy);
        var sentOn = sent?.CreatedUtc is { Length: >= 10 } s
            ? DateOnly.Parse(s[..10]) : (DateOnly?)null;

        var overdue = n.Status == "pending"
            && Domain.Compliance.ServicingTransfer.Overdue(
                sendBy, null, now);
        var sentLate = sentOn is { } so
            && Domain.Compliance.ServicingTransfer.SentLate(sendBy, so);

        return new ServicingNoticeDto(
            n.NoticeId, n.NoticeKind, n.TransferEffective,
            n.TransferorLabel, n.TransfereeLabel, n.SendBy, n.Status,
            overdue, sentLate,
            sent?.CreatedUtc, received?.CreatedUtc,
            evts.Select(e => new ServicingNoticeEventDto(
                e.Status, e.Actor, e.CreatedUtc)).ToList());
    }
}
