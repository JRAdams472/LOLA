using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;

namespace Lola.Application.Events;

/// Staff-opened condition — `manual` kind unless a future auto-writer
/// (M16–M18) supplies another. Every open writes the audit spine and
/// a `condition_opened` loan event.
public sealed record OpenConditionCommand(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower,
    string Kind, string Title, string? Detail, bool Blocking,
    string? LinkedEntity = null, string? LinkedEntityId = null);

public static class OpenConditionHandler
{
    public static async Task<ConditionDto> Handle(
        OpenConditionCommand c, IConditionRepository conditions,
        IApplicationRepository apps, ITenantAuditLog audit,
        ILoanEventLog events, Tasks.TaskEvaluator eval,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw DomainException.Forbidden(
                "conditions are a staff action");
        if (string.IsNullOrWhiteSpace(c.Title))
            throw DomainException.BadRequest("title is required");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, isBorrower: false, ct);

        var created = await conditions.OpenAsync(new ConditionDraft(
            c.AppId, c.Kind, c.Title, c.Detail, c.Blocking,
            c.LinkedEntity, c.LinkedEntityId, c.ActorId), ct);
        await audit.WriteAsync(c.ActorId, "condition_opened",
            "condition", created.ConditionId,
            AuditFields.InDoc(app.Doc),
            $"opened {c.Kind}: {c.Title}", ct);
        await events.WriteAsync(new LoanEventDraft(c.AppId,
            LoanEventKind.ConditionOpened, c.ActorId, "condition",
            created.ConditionId, FieldPath: $"conditions.{c.Kind}",
            AfterVal: created.State), ct);
        await eval.EvaluateApplicationAsync(c.TenantId, c.AppId, ct);
        return ConditionDto.Of(created);
    }
}
