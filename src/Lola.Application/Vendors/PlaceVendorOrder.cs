using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Security;

namespace Lola.Application.Vendors;

/// POST …/applications/{id}/vendor-orders (MPE-227) — staff order a
/// vendor *kind*; the mapper derives the minimum-field request from
/// the current document, the request lands `vault:`-encrypted, and
/// the outbox worker submits it asynchronously. Repeat submissions
/// of the same kind+payload dedupe on `request_hash`.
public sealed record PlaceVendorOrderCommand(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower,
    string Kind);

public sealed record VendorOrderAccepted(
    string OrderId, string Kind, bool Deduplicated);

public static class PlaceVendorOrderHandler
{
    public static async Task<VendorOrderAccepted> Handle(
        PlaceVendorOrderCommand c, IApplicationRepository apps,
        IVendorOrderRepository orders, IVendorAdapter adapter,
        IFieldCipher cipher, ITenantAuditLog audit,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw DomainException.Forbidden(
                "vendor ordering is a staff action");
        var kind = VendorGuards.Parse(c.Kind);
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, isBorrower: false, ct);
        if (!adapter.Kinds.Contains(kind))
            throw DomainException.BadRequest(
                $"configured adapter '{adapter.Adapter}' does not " +
                $"handle '{c.Kind}' orders");
        var request = VendorRequestMapper.Map(kind, app);
        var requestJson = VendorRequestMapper.RequestJson(request);
        var order = new VendorOrderRow(
            $"vo-{Guid.NewGuid():N}", c.AppId, VendorGuards.Token(kind),
            adapter.Adapter, null, "ordered",
            await cipher.EncryptAsync(requestJson, ct),
            null, null, c.ActorId, null, null, null,
            VendorRequestMapper.RequestHash(
                c.TenantId, c.AppId, kind, requestJson));
        var result = await orders.EnqueueAsync(
            order, $"vob-{Guid.NewGuid():N}", ct);
        if (!result.Existing)
        {
            await apps.RecordStepEventAsync(c.AppId,
                WorkflowPipeline.ServiceStep(kind), "ordered",
                c.ActorId, "vendor order placed", ct);
            await audit.WriteAsync(c.ActorId, "vendor_order",
                "application", c.AppId,
                AuditFields.ForPaths(
                    VendorRequestMapper.RequiredPaths(kind)),
                $"{VendorGuards.Token(kind)} order placed", ct);
        }
        return new VendorOrderAccepted(
            result.OrderId, VendorGuards.Token(kind), result.Existing);
    }
}
