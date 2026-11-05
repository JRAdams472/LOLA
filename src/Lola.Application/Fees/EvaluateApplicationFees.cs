using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;

namespace Lola.Application.Fees;

/// POST …/applications/{id}/fees (MPE-183) — maps the current
/// document to the PII-free fee scenario, enqueues eval+outbox row in
/// one transaction, and audits the request. The adapter call itself
/// is deferred to the outbox worker — the endpoint never blocks on
/// Zara. Duplicate requests (same app + same request payload) return
/// the original eval id rather than double-billing the adapter.
public sealed record EvaluateApplicationFeesCommand(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower,
    IReadOnlyList<string>? FeeCodes);

public sealed record FeeEvalAccepted(string EvalId, bool Deduplicated);

public static class EvaluateApplicationFeesHandler
{
    public static async Task<FeeEvalAccepted> Handle(
        EvaluateApplicationFeesCommand c, IApplicationRepository apps,
        IFeeRepository fees, IFeeService adapter,
        ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw Errors.DomainException.Forbidden(
                "fee evaluation is a staff action");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, isBorrower: false, ct);

        var scenario = FeeScenarioMapper.Map(app.Doc);
        var options = new FeeRequestOptions(c.FeeCodes);
        var payload = FeeWire.PayloadJson(scenario, options);
        var eval = new FeeEvalRecord(
            $"eval-{Guid.NewGuid():N}", c.AppId, adapter.Adapter,
            FeeWire.RequestHash(c.AppId, payload),
            System.Text.Json.JsonSerializer.Serialize(scenario, FeeWire.Json),
            "pending", null, null, c.ActorId, null, null);

        var result = await fees.EnqueueAsync(
            eval, $"ob-{Guid.NewGuid():N}", "eval", payload, ct);
        // Audit once — a deduplicated re-request adds no event, the
        // original `fee_eval` row already attributed the work.
        if (!result.Deduplicated)
            await audit.WriteAsync(c.ActorId, "fee_eval", "application",
                c.AppId, AuditFields.InDoc(app.Doc),
                "fee evaluation requested", ct);
        return new FeeEvalAccepted(result.EvalId, result.Deduplicated);
    }
}
