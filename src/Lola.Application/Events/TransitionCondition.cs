using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;

namespace Lola.Application.Events;

/// Condition lifecycle transition (M15 P3) — the handler owns the
/// reason contract (close + waive require one; waived is terminal and
/// carries the waiver reason on the audit spine); the repository
/// enforces the legal move itself. Every transition writes a matching
/// `condition_*` loan event.
public sealed record TransitionConditionCommand(
    string TenantId, string AppId, string ConditionId, string ActorId,
    bool ActorIsBorrower, string To, string? Reason);

public static class TransitionConditionHandler
{
    public static async Task<ConditionDto> Handle(
        TransitionConditionCommand c, IConditionRepository conditions,
        IApplicationRepository apps, ITenantAuditLog audit,
        ILoanEventLog events, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw DomainException.Forbidden(
                "condition transitions are a staff action");
        var to = c.To.ToLowerInvariant();
        if (to is not (ConditionState.Closed or ConditionState.Waived
            or ConditionState.Open))
            throw DomainException.BadRequest(
                $"unknown condition transition '{c.To}'");
        if (to is ConditionState.Closed or ConditionState.Waived
            && string.IsNullOrWhiteSpace(c.Reason))
            throw DomainException.BadRequest(
                $"a reason is required to {(
                    to == ConditionState.Waived ? "waive" : "close")}"
                + " a condition");

        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, isBorrower: false, ct);
        var existing = await conditions.GetAsync(c.ConditionId, ct);
        if (existing is null || existing.ApplicationId != c.AppId)
            throw DomainException.NotFound("condition not found");
        // Handler owns the transition contract (fake repos don't guard);
        // the SQLCipher impl re-checks as defense-in-depth.
        var legal = (existing.State, to) switch
        {
            (ConditionState.Open, ConditionState.Closed) => true,
            (ConditionState.Open, ConditionState.Waived) => true,
            (ConditionState.Closed, ConditionState.Open) => true,
            _ => false
        };
        if (!legal)
            throw DomainException.BadRequest(
                $"condition cannot move '{existing.State}' → '{to}'");

        var next = await conditions.TransitionAsync(
            c.ConditionId, to, c.Reason, ct);
        var action = to switch
        {
            ConditionState.Closed => "condition_closed",
            ConditionState.Waived => "condition_waived",
            _ => "condition_reopened"
        };
        await audit.WriteAsync(c.ActorId, action, "condition",
            c.ConditionId, AuditFields.InDoc(app.Doc),
            c.Reason ?? $"condition {to}", ct);
        var kind = to switch
        {
            ConditionState.Closed => LoanEventKind.ConditionClosed,
            ConditionState.Waived => LoanEventKind.ConditionWaived,
            _ => LoanEventKind.ConditionReopened
        };
        await events.WriteAsync(new LoanEventDraft(c.AppId, kind,
            c.ActorId, "condition", c.ConditionId,
            FieldPath: $"conditions.{next.Kind}",
            BeforeVal: existing.State, AfterVal: next.State), ct);
        return ConditionDto.Of(next);
    }
}
