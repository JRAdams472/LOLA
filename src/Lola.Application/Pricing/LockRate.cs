using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;

namespace Lola.Application.Pricing;

/// POST …/applications/{id}/lock (MPE-131) — converts a completed
/// eval into an active rate lock. Requires the eval to be `done` and
/// to contain the requested product+rate; expiry = locked + term.
/// Frozen paths persist on the row so the patch handlers can enforce
/// them without re-deriving the scenario.
public sealed record LockRateCommand(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower,
    string? EvalId, string ProductCode, decimal Rate,
    int? LockTermDays);

public sealed record LockResult(string LockId, string ExpiresUtc);

public static class LockRateHandler
{
    public static async Task<LockResult> Handle(
        LockRateCommand c, IApplicationRepository apps,
        IPricingRepository pricing, IRateLockRepository locks,
        ITenantAuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw DomainException.Forbidden("rate lock is a staff action");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, isBorrower: false, ct);

        if (await locks.GetActiveAsync(c.AppId, ct) is not null)
            throw DomainException.Conflict(
                "an active rate lock exists — unlock before re-locking");

        var eval = c.EvalId is not null
            ? await pricing.GetEvalAsync(c.EvalId, ct)
            : await pricing.GetLatestEvalAsync(c.AppId, ct);
        if (eval is null || eval.ApplicationId != c.AppId)
            throw DomainException.NotFound("no evaluation for this application");
        if (eval.Status != "done")
            throw DomainException.Conflict(
                "rate lock requires a completed evaluation");
        var result = PricingWire.ParseResult(eval.ResultJson)
            ?? throw DomainException.Conflict(
                "evaluation carried no parsed result");

        var product = result.Products.FirstOrDefault(
            p => p.ProductCode == c.ProductCode)
            ?? throw DomainException.BadRequest(
                $"product '{c.ProductCode}' was not evaluated");
        var offer = product.Offers.FirstOrDefault(
            o => o.Rate == c.Rate &&
                 (c.LockTermDays is null || o.LockTermDays == c.LockTermDays))
            ?? throw DomainException.BadRequest(
                $"rate {c.Rate} was not offered on '{c.ProductCode}'");

        var term = c.LockTermDays ?? offer.LockTermDays
            ?? throw DomainException.BadRequest(
                "no lock term — the offer carried none");
        var locked = clock.GetUtcNow();
        var expires = locked.AddDays(term);
        var row = new RateLockRecord(
            $"lock-{Guid.NewGuid():N}", c.AppId, eval.EvalId,
            c.ProductCode, c.Rate, offer.Price, term,
            offer.InvestorName,
            locked.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            expires.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            c.ActorId, "active", ScenarioMapper.FrozenPaths,
            null, null, null);
        await locks.InsertAsync(row, ct);
        await audit.WriteAsync(c.ActorId, "rate_lock", "application",
            c.AppId, [], $"{c.ProductCode} {c.Rate}% {term}d", ct);
        return new LockResult(row.LockId, row.ExpiresUtc);
    }
}
