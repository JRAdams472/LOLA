using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Events;

namespace Lola.Application.Vendors;

/// Signed validation feedback from a downstream source (M17 P2) —
/// the ingress endpoint (P3) verifies the signature and resolves the
/// tenant; this handler owns dedupe and the finding→domain mapping.
/// The actor is the source itself (`auto:{source}`) — system writes,
/// never staff impersonation.
public sealed record RecordValidationFeedbackCommand(
    string TenantId, ValidationFeedback Feedback);

/// What the receipt produced — `Replay` short-circuits every domain
/// write; `Action` is `condition_closed` | `condition_opened` |
/// `recorded` (a cleared finding with no matching open condition
/// still lands on the ledger + both spines).
public sealed record FeedbackOutcome(
    bool Replay, string? ConditionId, string Action);

public static class RecordValidationFeedbackHandler
{
    public static async Task<FeedbackOutcome> Handle(
        RecordValidationFeedbackCommand c,
        IVendorFeedbackRepository feedback,
        IConditionRepository conditions,
        IApplicationRepository apps, ITenantAuditLog audit,
        ILoanEventLog events, Tasks.TaskEvaluator eval,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var f = c.Feedback;
        if (!FindingKind.All.Contains(f.Finding))
            throw DomainException.BadRequest(
                $"unknown finding '{f.Finding}'");
        if (!FeedbackSeverity.All.Contains(f.Severity))
            throw DomainException.BadRequest(
                $"unknown severity '{f.Severity}'");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, f.ApplicationId, $"auto:{f.Source}",
            ActorScope.Staff, ct);
        var actor = $"auto:{f.Source}";
        var docClasses = AuditFields.InDoc(app.Doc);

        // Dedupe ledger first — INSERT OR IGNORE makes the receipt the
        // single write that decides; a replay returns before any
        // condition/event/task write (recorded no-op).
        var recorded = await feedback.TryRecordAsync(
            new VendorFeedbackReceipt(f.Source, f.ApplicationId,
                f.FeedbackId, f.Finding, f.Severity, f.LinkedEntity,
                f.LinkedEntityId), ct);
        if (!recorded) return new FeedbackOutcome(true, null, "replay");

        // Every accepted receipt lands on both spines — the finding
        // itself is metadata (kind + severity), never values.
        await audit.WriteAsync(actor, "validation_received",
            "vendor_feedback", f.FeedbackId, docClasses,
            $"{f.Source} {f.Finding}", ct);
        await events.WriteAsync(new LoanEventDraft(f.ApplicationId,
            LoanEventKind.ValidationReceived, actor, "vendor_feedback",
            f.FeedbackId, FieldPath: $"validation.{f.Finding}",
            AfterVal: f.Severity), ct);

        string? conditionId = null;
        var action = "recorded";
        if (f.Finding == FindingKind.Cleared)
        {
            var cond = f.ConditionId is null ? null
                : await conditions.GetAsync(f.ConditionId, ct);
            if (cond is { State: ConditionState.Open }
                && cond.ApplicationId == f.ApplicationId)
            {
                var next = await conditions.TransitionAsync(
                    cond.ConditionId, ConditionState.Closed,
                    $"{f.Source} validation cleared", ct);
                await audit.WriteAsync(actor, "condition_closed",
                    "condition", cond.ConditionId, docClasses,
                    $"{f.Source} cleared via {f.FeedbackId}", ct);
                await events.WriteAsync(new LoanEventDraft(
                    f.ApplicationId, LoanEventKind.ConditionClosed, actor,
                    "condition", cond.ConditionId,
                    FieldPath: $"conditions.{next.Kind}",
                    BeforeVal: ConditionState.Open,
                    AfterVal: next.State), ct);
                conditionId = cond.ConditionId;
                action = "condition_closed";
            }
        }
        else
        {
            // data_error|missing_field|exception → open a tracking
            // condition linked back to the ledger row. Exceptions and
            // error-severity findings block downstream work; advisory
            // findings (info|warning) stay non-blocking so they don't
            // flood the queue — the evaluator tasks only blockers.
            var blocking = f.Finding == FindingKind.Exception
                || f.Severity == FeedbackSeverity.Error;
            var created = await conditions.OpenAsync(new ConditionDraft(
                f.ApplicationId, ConditionKind.DataValidation,
                $"{f.Source} validation — {f.Finding}",
                Detail: $"{f.Source} {f.Finding}"
                    + $" (severity {f.Severity}, {f.FeedbackId})",
                Blocking: blocking,
                LinkedEntity: FeedbackLink.VendorFeedback,
                LinkedEntityId: f.FeedbackId, OpenedBy: actor), ct);
            await audit.WriteAsync(actor, "condition_opened",
                "condition", created.ConditionId, docClasses,
                $"{f.Source} {f.Finding} via {f.FeedbackId}", ct);
            await events.WriteAsync(new LoanEventDraft(f.ApplicationId,
                LoanEventKind.ConditionOpened, actor, "condition",
                created.ConditionId,
                FieldPath: $"conditions.{created.Kind}",
                AfterVal: created.State), ct);
            conditionId = created.ConditionId;
            action = "condition_opened";
        }

        // Convergent queue update — a closed condition resolves its
        // task; a new blocking feedback condition derives a
        // `validation_exception` task (the evaluator keys off
        // linked_entity) in the same pass.
        await eval.EvaluateApplicationAsync(c.TenantId,
            f.ApplicationId, ct);
        return new FeedbackOutcome(false, conditionId, action);
    }
}
