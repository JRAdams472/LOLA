using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;

namespace Lola.Application.Vendors;

/// GET …/applications/{id}/vendor-orders — staff order history for
/// the application's Steps surface. Metadata only: the encrypted
/// request/result payloads never cross this read (the audited detail
/// endpoint is the only decrypt path).
public sealed record ListVendorOrdersQuery(
    string TenantId, string AppId, string ActorId,
    bool ActorIsBorrower);

public static class ListVendorOrdersHandler
{
    public static async Task<IReadOnlyList<VendorOrderDto>> Handle(
        ListVendorOrdersQuery q, IApplicationRepository apps,
        IVendorOrderRepository orders, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden(
                "vendor orders are a staff surface");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, isBorrower: false, ct);
        var rows = await orders.ListByApplicationAsync(q.AppId, ct);
        return rows.Select(r => new VendorOrderDto(
            r.OrderId, r.ApplicationId, r.Kind, r.Adapter,
            r.ExternalId, r.Status, r.Error, r.OrderedBy,
            r.CreatedUtc, r.SubmittedUtc, r.CompletedUtc,
            null)).ToList();
    }
}
