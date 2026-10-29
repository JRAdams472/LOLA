using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;
using Lola.Domain.Applications;

namespace Lola.Application.Audit;

/// Audited PII reveal (MPE-117, invariant 8) — the explicit staff
/// action that returns the UNMASKED document. Every reveal writes a
/// `reveal` audit event carrying the actor, the catalog PII classes
/// exposed, and the operator-supplied reason. Masked GET is unchanged;
/// this is the only endpoint that skips DocMasker.
public sealed record RevealApplicationQuery(
    string TenantId, string AppId, string ActorId, string Reason,
    bool ActorIsBorrower);

public static class RevealApplicationHandler
{
    public static async Task<ApplicationDto?> Handle(
        RevealApplicationQuery q, IApplicationRepository apps,
        ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden("PII reveal is staff-only");
        if (string.IsNullOrWhiteSpace(q.Reason))
            throw new ArgumentException("a reveal reason is required");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, isBorrower: false, ct);
        await audit.WriteAsync(q.ActorId, "reveal", "application",
            q.AppId, AuditFields.InDoc(app.Doc), q.Reason, ct);
        // Deliberately unmasked — the reveal IS the unmask.
        return new ApplicationDto(app.Id, app.Status, app.PipelineStep,
            app.BorrowerUserId, app.CurrentVersion, app.TridClockStart,
            TridClock.DeadlineUtc(app.TridClockStart),
            app.SubmittedUtc, app.Doc.CloneRoot());
    }
}
