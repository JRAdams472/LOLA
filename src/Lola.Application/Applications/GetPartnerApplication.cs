using Lola.Application.Admin;
using Lola.Application.Errors;
using Lola.Domain.Applications;

namespace Lola.Application.Applications;

/// Partner-scoped application detail (M19): metadata + loan amount,
/// authorized on a minimal-metadata scoped query BEFORE the payload
/// is touched — `GetScopedMetaAsync` answers missing-vs-unlinked
/// without loading the doc, and `loan_amount` is a catalog non-PII
/// field extracted server-side (never the staff document, never
/// rate-lock or document-intelligence internals).
public sealed record GetPartnerApplicationQuery(
    string TenantId, string AppId, string ActorId);

public sealed record PartnerApplicationDto(
    string Id, string Status, string PipelineStep,
    int CurrentVersion, string? SubmittedUtc, string? LoanAmount);

public static class GetPartnerApplicationHandler
{
    public static async Task<PartnerApplicationDto?> Handle(
        GetPartnerApplicationQuery q, IApplicationRepository apps,
        Audit.ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var meta = await apps.GetScopedMetaAsync(
            q.AppId, ActorScope.Partner, q.ActorId, ct)
            ?? throw DomainException.NotFound("application not found");
        if (!meta.InScope)
            throw DomainException.Forbidden(
                "partner scope covers linked applications only");
        // loan_amount carries no catalog PiiClass — a json_extract on
        // the current version, never a payload hand-off.
        var amount = await apps.GetDocValueAsync(
            q.AppId, "$.loan_amount", ct);
        await audit.WriteAsync(q.ActorId, "read", "application", q.AppId,
            ["status"], "partner metadata", ct);
        return new PartnerApplicationDto(meta.Id, meta.Status,
            meta.PipelineStep, (int)meta.CurrentVersion,
            meta.SubmittedUtc, amount);
    }
}
