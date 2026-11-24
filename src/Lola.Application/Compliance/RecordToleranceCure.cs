using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Application.Identity;

namespace Lola.Application.Compliance;

/// POST …/applications/{id}/tolerance/cure (M21 P2) — records a
/// tolerance cure: closes the application's open `tolerance_cure`
/// condition with the cure reason and writes the attributed event.
/// No open cure → 409.
public sealed record RecordToleranceCureCommand(
    string TenantId, string AppId, string ActorId, ActorScope Scope,
    string Reason);

public sealed record CureResult(string ConditionId);

public static class RecordToleranceCureHandler
{
    public static async Task<CureResult> Handle(
        RecordToleranceCureCommand c, IApplicationRepository apps,
        IConditionRepository conditions, ILoanEventLog events,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "curing a tolerance violation is a staff action");
        await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, ActorScope.Staff, ct);
        if (string.IsNullOrWhiteSpace(c.Reason))
            throw Errors.DomainException.BadRequest(
                "cure reason is required");

        var open = (await conditions.ListByApplicationAsync(
                c.AppId, ConditionState.Open, ct))
            .FirstOrDefault(k => k.Kind == ConditionKind.ToleranceCure)
            ?? throw Errors.DomainException.Conflict(
                "no open tolerance violation to cure");

        await conditions.TransitionAsync(open.ConditionId,
            ConditionState.Closed, c.Reason, ct);
        await events.WriteAsync(new LoanEventDraft(c.AppId,
            LoanEventKind.ToleranceCure, c.ActorId,
            "condition", open.ConditionId, "tolerance", null,
            ConditionState.Open, ConditionState.Closed), ct);
        return new CureResult(open.ConditionId);
    }
}
