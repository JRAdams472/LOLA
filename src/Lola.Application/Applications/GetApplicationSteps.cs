using Lola.Application.Admin;
using Lola.Application.Errors;

namespace Lola.Application.Applications;

/// Per-application pipeline step history — the append-only event log
/// behind the named-step seam. Staff-only: the step vocabulary is
/// internal workflow detail (M12 hardening gate).
public sealed record GetApplicationStepsQuery(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class GetApplicationStepsHandler
{
    public static async Task<IReadOnlyList<StepEventDto>> Handle(
        GetApplicationStepsQuery q, IApplicationRepository apps,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope != ActorScope.Staff)
            throw DomainException.Forbidden("step history is staff-only");
        await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.Scope, ct);
        return await apps.ListStepEventsAsync(q.AppId, ct);
    }
}
