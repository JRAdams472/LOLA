using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;

namespace Lola.Application.Pricing;

/// POST …/applications/{id}/price (MPE-130) — maps the current
/// document to the PII-free scenario, enqueues eval+outbox row in one
/// transaction, and audits the request. The adapter call itself is
/// deferred to the outbox worker — the endpoint never blocks on MPEx.
/// Duplicate requests (same app + same request payload) return the
/// original eval id rather than double-billing the adapter.
public sealed record PriceApplicationCommand(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower,
    IReadOnlyList<string>? Products, decimal? Rate, int? LockTermDays,
    string? AsOf, bool EligibilityOnly);

public sealed record PriceResult(string EvalId, bool Deduplicated);

public static class PriceApplicationHandler
{
    public static async Task<PriceResult> Handle(
        PriceApplicationCommand c, IApplicationRepository apps,
        IPricingRepository pricing, IPricingService adapter,
        ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw DomainException.Forbidden("pricing is a staff action");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, isBorrower: false, ct);
        // `asOf` selects a publication snapshot (MPE-132): "draft" or
        // an RFC3339 timestamp — anything else is a client error.
        if (c.AsOf is not null && c.AsOf != "draft" &&
            !DateTimeOffset.TryParse(c.AsOf, out _))
            throw DomainException.BadRequest(
                "asOf must be 'draft' or an RFC3339 timestamp");

        var scenario = ScenarioMapper.Map(app.Doc,
            lockTermDays: c.LockTermDays, asOf: c.AsOf);
        var options = new PricingRequestOptions(
            c.Products, c.Rate, c.LockTermDays, c.AsOf, c.EligibilityOnly);
        var payload = PricingWire.PayloadJson(scenario, options);
        var eval = new PricingEvalRecord(
            $"eval-{Guid.NewGuid():N}", c.AppId, adapter.Adapter, c.AsOf,
            PricingWire.RequestHash(c.AppId, payload),
            PricingWire.LoanJson(scenario).ToJsonString(),
            "pending", null, null, c.ActorId, null, null);

        // `asOf` re-evals tag `reval` so the queue can distinguish a
        // historic-snapshot request from a live eval (MPE-132).
        var result = await pricing.EnqueueAsync(
            eval, $"ob-{Guid.NewGuid():N}",
            c.AsOf is null ? "eval" : "reval", payload, ct);
        // Audit once — a deduplicated re-request adds no event, the
        // original `price_eval` row already attributed the work.
        if (!result.Deduplicated)
            await audit.WriteAsync(c.ActorId, "price_eval", "application",
                c.AppId, AuditFields.InDoc(app.Doc),
                "pricing evaluation requested", ct);
        return new PriceResult(result.EvalId, result.Deduplicated);
    }
}
