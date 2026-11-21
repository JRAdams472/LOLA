using Lola.Application.Admin;

namespace Lola.Application.Applications;

/// Partner-scoped application detail (M19/MPE-272): the linked-app
/// metadata projection only — never the staff document. Phase 4
/// (MPE-266) grows this into the purpose-specific partner DTO; until
/// then the partner sees loan status, not field data.
public sealed record GetPartnerApplicationQuery(
    string TenantId, string AppId, string ActorId);

public sealed record PartnerApplicationDto(
    string Id, string Status, string PipelineStep,
    int CurrentVersion, string? SubmittedUtc);

public static class GetPartnerApplicationHandler
{
    public static async Task<PartnerApplicationDto?> Handle(
        GetPartnerApplicationQuery q, IApplicationRepository apps,
        Audit.ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, ActorScope.Partner, ct);
        await audit.WriteAsync(q.ActorId, "read", "application", q.AppId,
            ["status"], "partner metadata", ct);
        return new PartnerApplicationDto(app.Id, app.Status,
            app.PipelineStep, app.CurrentVersion, app.SubmittedUtc);
    }
}
