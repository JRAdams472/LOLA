using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;

namespace Lola.Application.Fees;

/// POST …/applications/{id}/fees (MPE-183, extended MPE-188) — maps
/// the current document to the PII-free fee scenario, enqueues one
/// eval+outbox row per requested variant in a shared batch, and
/// audits the request. The adapter call itself is deferred to the
/// outbox worker — the endpoint never blocks on Zara, and the batch
/// drains in parallel. Duplicate requests (same app + same variant
/// payload) return the original eval id — dedup is per-variant.
public sealed record EvaluateApplicationFeesCommand(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower,
    IReadOnlyList<string>? FeeCodes,
    IReadOnlyList<FeeVariant>? Variants = null);

public sealed record FeeEvalAccepted(
    string BatchId, IReadOnlyList<string> EvalIds,
    bool Deduplicated);

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
        var base_ = FeeScenarioMapper.Map(app.Doc);
        var options = new FeeRequestOptions(c.FeeCodes);

        // Tag = label ?? loanType ?? "" — the override/merge key, so
        // a batch can't carry two variants resolving to the same tag.
        var variants = c.Variants is { Count: > 0 }
            ? c.Variants : [new FeeVariant(null, null, null)];
        var tags = variants
            .Select(v => v.Label ?? v.LoanType ?? "").ToList();
        if (tags.Distinct().Count() != tags.Count)
            throw Errors.DomainException.BadRequest(
                "variant names/loan types must be unique per request");

        var batchId = $"bth-{Guid.NewGuid():N}";
        var evalIds = new List<string>(variants.Count);
        var enqueuedAny = false;
        foreach (var (v, tag) in variants.Zip(tags))
        {
            var scenario = base_ with
            {
                Loan = base_.Loan with
                {
                    Type = v.LoanType ?? base_.Loan.Type
                },
                Borrower = base_.Borrower with
                {
                    DiscountPoints = v.DiscountPoints
                        ?? base_.Borrower.DiscountPoints
                }
            };
            var payload = FeeWire.PayloadJson(scenario, options);
            var eval = new FeeEvalRecord(
                $"eval-{Guid.NewGuid():N}", c.AppId, adapter.Adapter,
                FeeWire.RequestHash(c.AppId, payload),
                System.Text.Json.JsonSerializer.Serialize(
                    scenario, FeeWire.Json),
                "pending", null, null, c.ActorId, null, null,
                batchId, tag);
            var result = await fees.EnqueueAsync(
                eval, $"ob-{Guid.NewGuid():N}", "eval", payload, ct);
            evalIds.Add(result.EvalId);
            enqueuedAny |= !result.Deduplicated;
        }
        // Audit once per request — a fully-deduplicated re-request
        // adds no event; the original `fee_eval` already attributed.
        if (enqueuedAny)
            await audit.WriteAsync(c.ActorId, "fee_eval", "application",
                c.AppId, AuditFields.InDoc(app.Doc),
                "fee evaluation requested", ct);
        return new FeeEvalAccepted(batchId, evalIds, !enqueuedAny);
    }
}
