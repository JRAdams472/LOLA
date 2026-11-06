using Lola.Application.Pricing;
using Lola.Infrastructure.Pricing;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// Stub pricing adapter proofs: the default provider returns a
/// deterministic two-product eval (conventional always eligible, FHA
/// below the jumbo line) and honors the Products filter — the seam
/// MPEx swaps behind via IPricingService alone (ADR-0007).
public sealed class StubPricingAdapterTests
{
    private static readonly PricingScenario Scenario = new(
        new LoanSection("purchase", "conventional", 400000m, 360,
            null, null, null, null, null),
        new PropertySection(null, null, "TX", 500000m, 500000m),
        new BorrowerSection(740, "FICO", null, null),
        new ScenarioSection(30, null));

    private readonly StubPricingAdapter _svc = new();

    [Fact]
    public async Task Evaluate_returns_two_products_with_offers()
    {
        var eval = await _svc.EvaluateAsync(
            Scenario, new PricingRequestOptions());

        eval.Adapter.ShouldBe("stub");
        eval.EvalId.ShouldStartWith("eval-");
        eval.Products.Select(p => p.ProductCode)
            .ShouldBe(["conv-30-fixed", "fha-30-fixed"]);
        eval.Products.ShouldAllBe(p => p.Eligible);
        eval.Products[0].Offers.Select(o => o.Rate)
            .ShouldBe([6.500m, 6.250m]);
    }

    [Fact]
    public async Task Evaluate_fha_is_ineligible_above_the_jumbo_line()
    {
        var jumbo = Scenario with
        {
            Loan = Scenario.Loan with { Amount = 800000m }
        };
        var eval = await _svc.EvaluateAsync(
            jumbo, new PricingRequestOptions());

        var fha = eval.Products.Single(p => p.ProductCode == "fha-30-fixed");
        fha.Eligible.ShouldBeFalse();
        fha.IneligibilityReasons.ShouldContain(
            "loan amount exceeds FHA limit");
    }

    [Fact]
    public async Task Evaluate_products_filter_limits_the_set()
    {
        var eval = await _svc.EvaluateAsync(
            Scenario, new PricingRequestOptions(["conv-30-fixed"]));

        eval.Products.Select(p => p.ProductCode)
            .ShouldBe(["conv-30-fixed"]);
    }
}
