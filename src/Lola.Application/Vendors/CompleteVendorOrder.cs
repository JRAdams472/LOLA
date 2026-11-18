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
    HashMismatch // right order, wrong request_hash
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
        var order = await repo.FindByExternalIdAsync(
            VendorGuards.Token(cmd.Kind), cmd.ExternalId, ct);
        if (order is null) return VendorCompletion.NotFound;
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
