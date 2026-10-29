using Lola.Application.Pricing;

namespace Lola.Infrastructure.Pricing;

/// Deterministic stand-in pricing adapter (MPE-71 P1): proves the
/// seam end-to-end before MPEx is wired, and is the second-adapter
/// reference for the swap test — implementing `IPricingService` is the
/// whole contract (ADR-0007). Returns two products: a conventional
/// eligible one and an FHA one eligible only below the jumbo line.
public sealed class StubPricingAdapter : IPricingService
{
    public const string AdapterName = "stub";

    public string Adapter => AdapterName;

    public Task<PricingEvaluation> EvaluateAsync(
        PricingScenario s, PricingRequestOptions options,
        CancellationToken ct = default)
    {
        var products = new List<PricedProduct>
        {
            new("conv-30-fixed", "Conventional 30-year fixed", true, [],
            [
                new RateOffer(6.500m, 99.250m, 0.250m, 30, "Stub Investor A"),
                new RateOffer(6.250m, 98.750m, 0.250m, 45, "Stub Investor A"),
            ]),
            new("fha-30-fixed", "FHA 30-year fixed",
                s.Loan.Amount is null or <= 766_550m,
                s.Loan.Amount > 766_550m ? ["loan amount exceeds FHA limit"] : [],
            [
                new RateOffer(6.125m, 99.500m, 0.125m, 30, "Stub Investor B"),
            ]),
        };
        var filtered = options.Products is { Count: > 0 } wanted
            ? products.Where(p => wanted.Contains(p.ProductCode)).ToList()
            : products;
        return Task.FromResult(new PricingEvaluation(
            $"eval-{Guid.NewGuid():N}", AdapterName, options.AsOf,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            filtered));
    }
}
