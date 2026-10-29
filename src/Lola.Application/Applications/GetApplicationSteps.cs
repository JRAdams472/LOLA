using Lola.Application.Admin;

namespace Lola.Application.Applications;

/// Per-application pipeline step history — the append-only event log
/// behind the named-step seam; borrower actors stay own-draft scoped
/// through the same RequireAppAsync guard.
public sealed record GetApplicationStepsQuery(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower);

public static class GetApplicationStepsHandler
{
    public static async Task<IReadOnlyList<StepEventDto>> Handle(
        GetApplicationStepsQuery q, IApplicationRepository apps,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.ActorIsBorrower, ct);
        return await apps.ListStepEventsAsync(q.AppId, ct);
    }
}
