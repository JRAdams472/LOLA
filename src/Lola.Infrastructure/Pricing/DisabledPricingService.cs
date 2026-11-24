using Lola.Application.Errors;
using Lola.Application.Pricing;

namespace Lola.Infrastructure.Pricing;

/// Fail-closed IPricingService for PRICING_ADAPTER=disabled (MPE-277):
/// the capability is explicitly off — every call surfaces a 503 rather
/// than simulated financial results. Never registered for `stub` in
/// Production (the profile validator rejects that combination outright).
public sealed class DisabledPricingService : IPricingService
{
    public string Adapter => "disabled";

    public Task<PricingEvaluation> EvaluateAsync(
        PricingScenario scenario, PricingRequestOptions options,
        CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "pricing capability is disabled by configuration");
}
