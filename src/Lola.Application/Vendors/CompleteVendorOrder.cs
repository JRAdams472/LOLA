using System.Text.Json.Nodes;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Security;

namespace Lola.Application.Vendors;

/// One completion delivery — the webhook ingress and the poll drain
/// both land here. `RequestHash` binds the delivery to the order it
/// answers; `Report` is the vendor's normalized opaque document.
public sealed record CompleteVendorOrderCommand(
    VendorKind Kind, string ExternalId, string? RequestHash,
    string Outcome, string? FailureReason, JsonObject Report,
    string Actor);

/// Completion verdict — the endpoint maps these to 200/200/404/409.
public enum VendorCompletion
{
    Applied,    // order transitioned + step event written
    Duplicate,  // already terminal — replay, no-op (still 200)
    NotFound,   // unknown external id
    HashMismatch, // right order, wrong request_hash
    Ambiguous   // vendor reused the external id — cannot disambiguate
}

/// Idempotent result application (MPE-228): the repository's
/// status-guarded UPDATE means a duplicate delivery can never write
/// a second `result_json` or a second step event — the handler's
/// `Duplicate` verdict exists purely to report the no-op.
public sealed class CompleteVendorOrderHandler(
    IVendorOrderRepository repo, IFieldCipher cipher,
    ITenantAuditLog audit)
{
    public async Task<VendorCompletion> Handle(
        CompleteVendorOrderCommand cmd, CancellationToken ct)
    {
        // A vendor may dedupe identical submissions onto one tracking
        // id, so several of our orders can share it. Prefer the single
        // still-open match; all-terminal = replay; several open = the
        // delivery can't be attributed (409, vendor must resolve).
        var matches = await repo.ListByExternalIdAsync(
            VendorGuards.Token(cmd.Kind), cmd.ExternalId, ct);
        if (matches.Count == 0) return VendorCompletion.NotFound;
        if (cmd.RequestHash is not null)
        {
            matches = matches.Where(m => m.RequestHash == cmd.RequestHash)
                .ToList();
            if (matches.Count == 0) return VendorCompletion.HashMismatch;
        }
        var open = matches.Where(
            m => m.Status is "ordered" or "submitted").ToList();
        if (open.Count == 0) return VendorCompletion.Duplicate;
        if (open.Count > 1) return VendorCompletion.Ambiguous;
        var order = open[0];
        if (cmd.RequestHash is not null &&
            order.RequestHash != cmd.RequestHash)
            return VendorCompletion.HashMismatch;
        if (order.Status is not ("ordered" or "submitted"))
            return VendorCompletion.Duplicate;

        var outbox = await repo.FindOutboxByOrderAsync(
            order.OrderId, ct);
        var resultJson = await cipher.EncryptAsync(
            VendorRequestMapper.ResultJson(new VendorResult(
                cmd.Kind, cmd.Outcome, cmd.FailureReason,
                cmd.Report)), ct);
        await repo.CompleteAsync(order.OrderId, order.ApplicationId,
            resultJson, outbox?.OutboxId ?? "", cmd.Outcome,
            WorkflowPipeline.ServiceStep(cmd.Kind), cmd.Actor, ct);
        await audit.WriteAsync(cmd.Actor, "vendor_result",
            "vendor_order", order.OrderId, ["VendorReport"],
            "vendor order completed", ct);
        return VendorCompletion.Applied;
    }
}
