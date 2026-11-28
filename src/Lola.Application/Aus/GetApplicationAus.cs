using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;
using Lola.Application.Security;

namespace Lola.Application.Aus;

/// GET …/applications/{id}/aus (M25 P2) — posture read: latest result
/// per vendor with findings + append-only lineage. Staff-facing at
/// both layers; the raw response XML NEVER leaves this surface.
public sealed record GetApplicationAusQuery(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class GetApplicationAusHandler
{
    public static async Task<AusReportDto> Handle(
        GetApplicationAusQuery q, IApplicationRepository apps,
        IAusResultsStore store, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope != ActorScope.Staff)
            throw DomainException.Forbidden("aus posture is staff-facing");
        await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, ActorScope.Staff, ct);

        var rows = await store.ListResultsAsync(q.AppId, ct);
        var latest = rows
            .GroupBy(r => r.Vendor)
            .Select(g => g.OrderByDescending(r => r.CreatedUtc).First())
            .ToList();

        var vendors = new List<AusVendorPosture>();
        foreach (var r in latest)
        {
            var findings = await store.ListFindingsAsync(r.ResultId, ct);
            vendors.Add(new AusVendorPosture(r.Vendor, r.Recommendation,
                r.RiskClass, r.CaseId, r.CreatedUtc, r.FindingsCount,
                findings.Select(f => new AusFindingDto(f.VendorCode,
                    f.Category, f.Severity, f.Message, f.ConditionId))
                    .ToList()));
        }

        return new AusReportDto(vendors,
            rows.OrderByDescending(r => r.CreatedUtc)
                .Select(r => new AusLineageEntry(r.ResultId, r.Vendor,
                    r.Recommendation, r.CreatedUtc, r.FindingsCount))
                .ToList());
    }
}
