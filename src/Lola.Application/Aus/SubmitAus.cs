using Lola.Application.Admin;
using Lola.Application.Errors;
using Lola.Application.Security;
using Lola.Application.Applications;
using Lola.Application.Vendors;

namespace Lola.Application.Aus;

/// POST …/applications/{id}/aus/submit?vendor=du|lpa (M25 P2) — thin
/// staff-facing wrapper over the vendor-order flow: validates the GSE
/// vendor parameter, then places a kind=aus vendor order through the
/// existing mapper/outbox/adapter seam. In single-adapter deployments
/// the configured adapter must cover the requested vendor — the
/// adapter stamps its identity on the order row.
public sealed record SubmitAusCommand(
    string TenantId, string AppId, string ActorId, ActorScope Scope,
    string Vendor);

public static class SubmitAusHandler
{
    public static readonly IReadOnlySet<string> Vendors =
        new HashSet<string>(System.StringComparer.Ordinal) { "du", "lpa" };

    public static async Task<VendorOrderAccepted> Handle(
        SubmitAusCommand c, Applications.IApplicationRepository apps,
        IVendorOrderRepository orders, IVendorAdapter adapter,
        IFieldCipher cipher, Audit.ITenantAuditLog audit,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw DomainException.Forbidden("aus submission is a staff action");
        var vendor = c.Vendor?.Trim().ToLowerInvariant() ?? "";
        if (!Vendors.Contains(vendor))
            throw DomainException.BadRequest(
                $"unknown aus vendor '{c.Vendor}' — expected du|lpa");
        // In real deployments the adapter identity IS the vendor; the
        // stub serves both for demo/e2e. A mismatch in a real
        // deployment means misconfiguration — fail fast.
        if (adapter.Adapter is "du" or "lpa" && adapter.Adapter != vendor)
            throw DomainException.BadRequest(
                $"configured adapter '{adapter.Adapter}' cannot submit to '{vendor}'");
        return await PlaceVendorOrderHandler.Handle(
            new PlaceVendorOrderCommand(c.TenantId, c.AppId, c.ActorId,
                c.Scope, "aus"),
            apps, orders, adapter, cipher, audit, ct);
    }
}
