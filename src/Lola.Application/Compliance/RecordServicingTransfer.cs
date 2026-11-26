using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Application.Identity;

namespace Lola.Application.Compliance;

/// POST …/applications/{id}/respa/servicing/transfer (M23 P2) —
/// records a servicing transfer, creating BOTH statutory notice
/// obligations (transferor: effective − 15d; transferee: effective
/// + 15d) as pending rows with their first notice-events, plus ONE
/// hash-chained `servicing_notice` loan event per notice (status
/// summaries only).
public sealed record RecordServicingTransferCommand(
    string TenantId, string AppId, string ActorId, ActorScope Scope,
    string TransferEffective, string TransferorLabel,
    string TransfereeLabel);

public static class RecordServicingTransferHandler
{
    public static async Task<IReadOnlyList<ServicingNoticeDto>> Handle(
        RecordServicingTransferCommand c, IApplicationRepository apps,
        IRespaRepository respa, ILoanEventLog events,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "servicing-transfer recording is a staff action");
        await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, ActorScope.Staff, ct);

        if (!DateOnly.TryParse(c.TransferEffective, out var effective))
            throw Errors.DomainException.BadRequest(
                "transfer_effective must be an ISO date");
        if (string.IsNullOrWhiteSpace(c.TransferorLabel)
            || string.IsNullOrWhiteSpace(c.TransfereeLabel))
            throw Errors.DomainException.BadRequest(
                "transferor and transferee labels are required");

        var notices = new List<ServicingNoticeDto>();
        foreach (var kind in new[]
                 { Domain.Compliance.ServicingTransfer.Kind.Transferor,
                   Domain.Compliance.ServicingTransfer.Kind.Transferee })
        {
            var sendBy = Domain.Compliance.ServicingTransfer
                .SendBy(kind, effective);
            var record = await respa.AddNoticeAsync(c.AppId,
                kind.ToString().ToLowerInvariant(),
                effective.ToString("yyyy-MM-dd"),
                c.TransferorLabel.Trim(), c.TransfereeLabel.Trim(),
                sendBy.ToString("yyyy-MM-dd"), c.ActorId, ct);

            await events.WriteAsync(new LoanEventDraft(c.AppId,
                LoanEventKind.ServicingNotice, c.ActorId,
                "servicing_notice", record.NoticeId, "compliance", null,
                null,
                $"{record.NoticeKind}:pending;send_by:{record.SendBy}"), ct);

            notices.Add(await GetApplicationRespaHandler.ToDto(
                respa, record, ct));
        }
        return notices;
    }
}
