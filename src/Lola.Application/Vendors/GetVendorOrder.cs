using System.Text.Json.Nodes;
using Lola.Application.Admin;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Security;

namespace Lola.Application.Vendors;

/// GET …/vendor-orders/{orderId} — staff detail read. Decrypts the
/// stored result envelope and audits it: the report can carry
/// vendor-returned PII (scores, employer), so the read is attributed
/// like a reveal — field classes only, never values.
public sealed record GetVendorOrderQuery(
    string TenantId, string AppId, string OrderId, string ActorId,
    bool ActorIsBorrower);

public static class GetVendorOrderHandler
{
    public static async Task<VendorOrderDto?> Handle(
        GetVendorOrderQuery q, IVendorOrderRepository orders,
        IFieldCipher cipher, ITenantAuditLog audit,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden(
                "vendor orders are a staff surface");
        var row = await orders.GetAsync(q.OrderId, ct);
        if (row is null || row.ApplicationId != q.AppId)
            return null;
        JsonObject? result = null;
        if (row.ResultJson is { } stored)
        {
            var plaintext = await cipher.DecryptAsync(stored, ct);
            result = JsonNode.Parse(plaintext)?.AsObject();
            await audit.WriteAsync(q.ActorId, "vendor_result",
                "vendor_order", row.OrderId,
                ["VendorReport"],
                "vendor result read", ct);
        }
        return new VendorOrderDto(
            row.OrderId, row.ApplicationId, row.Kind, row.Adapter,
            row.ExternalId, row.Status, row.Error, row.OrderedBy,
            row.CreatedUtc, row.SubmittedUtc, row.CompletedUtc,
            result);
    }
}
