using Lola.Application.Admin;
using Lola.Domain.Applications;
using Lola.Domain.Identity;

namespace Lola.Application.Applications;

public sealed record GetApplicationQuery(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower);

public static class GetApplicationHandler
{
    public static async Task<ApplicationDto?> Handle(
        GetApplicationQuery q, IApplicationRepository apps,
        Audit.ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.ActorIsBorrower, ct);
        await audit.WriteAsync(q.ActorId, "read", "application", q.AppId,
            Audit.AuditFields.InDoc(app.Doc), null, ct);
        DocMasker.MaskInPlace(app.Doc);
        return new ApplicationDto(app.Id, app.Status, app.PipelineStep,
            app.BorrowerUserId, app.CurrentVersion, app.TridClockStart,
            TridClock.DeadlineUtc(app.TridClockStart),
            app.SubmittedUtc, app.Doc.CloneRoot());
    }
}
