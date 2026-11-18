using System.Text.Json.Nodes;
using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Security;

namespace Lola.Application.Vendors;

/// GET …/applications/{id}/vendor-orders — staff order history for
/// the application's Steps surface. Metadata + a one-line normalized
/// summary per order: completed orders decrypt their result envelope
/// for the summary — each of those reads is audited with field
/// classes only (reports can carry vendor-returned PII). The raw
/// request/result documents never cross this read.
public sealed record ListVendorOrdersQuery(
    string TenantId, string AppId, string ActorId,
    bool ActorIsBorrower);

public static class ListVendorOrdersHandler
{
    public static async Task<IReadOnlyList<VendorOrderDto>> Handle(
        ListVendorOrdersQuery q, IApplicationRepository apps,
        IVendorOrderRepository orders, IFieldCipher cipher,
        ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.ActorIsBorrower)
            throw DomainException.Forbidden(
                "vendor orders are a staff surface");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, isBorrower: false, ct);
        var rows = await orders.ListByApplicationAsync(q.AppId, ct);
        var dtos = new List<VendorOrderDto>(rows.Count);
        foreach (var r in rows)
        {
            string? summary = null;
            if (r.ResultJson is { } stored)
            {
                var report = JsonNode
                    .Parse(await cipher.DecryptAsync(stored, ct))
                    ?.AsObject()?["report"] as JsonObject;
                summary = VendorOrderSummary.Summarize(
                    VendorGuards.Parse(r.Kind), report);
                await audit.WriteAsync(q.ActorId, "vendor_result",
                    "vendor_order", r.OrderId, ["VendorReport"],
                    "vendor result read", ct);
            }
            dtos.Add(new VendorOrderDto(
                r.OrderId, r.ApplicationId, r.Kind, r.Adapter,
                r.ExternalId, r.Status, r.Error, r.OrderedBy,
                r.CreatedUtc, r.SubmittedUtc, r.CompletedUtc,
                null, summary));
        }
        return dtos;
    }
}
