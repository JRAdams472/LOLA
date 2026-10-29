using Lola.Application.Admin;
using Lola.Application.Errors;
using Lola.Domain.Applications;

namespace Lola.Application.Applications;

/// Reg B retention report (MPE-118) — per-application 25-month windows
/// and the tenant's earliest-clear date (the last window to close).
/// Report-only: nothing here disposes of an application. Staff-only —
/// a borrower gets 403 like every other audit/compliance surface.
public sealed record GetTenantRetentionQuery(
    string TenantId, string ActorId, bool ActorIsBorrower);

public sealed record ApplicationRetention(
    string AppId, string CreatedUtc, DateTimeOffset RetentionUntilUtc);

public sealed record TenantRetentionReport(
    IReadOnlyList<ApplicationRetention> Applications,
    DateTimeOffset? EarliestClearUtc);

public static class GetTenantRetentionHandler
{
    public static async Task<TenantRetentionReport> Handle(
        GetTenantRetentionQuery q, IApplicationRepository apps,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden("retention reports are staff-only");
        var windows = await apps.RetentionWindowsAsync(ct);
        var apps_ = windows
            .Select(w => new ApplicationRetention(
                w.Id, w.CreatedUtc, RetentionPolicy.Until(w.CreatedUtc)))
            .ToList();
        return new TenantRetentionReport(apps_,
            apps_.Count == 0 ? null : apps_.Max(a => a.RetentionUntilUtc));
    }
}
