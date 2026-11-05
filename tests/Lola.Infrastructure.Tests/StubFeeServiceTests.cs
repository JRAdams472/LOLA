using Lola.Application.Fees;
using Lola.Infrastructure.Fees;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// MPE-182 stub-adapter proofs: the default fee provider returns a
/// deterministic itemized eval spanning LE sections A/B/C/E, honors
/// the FeeCodes filter, and derives amount-driven fees (transfer
/// tax) from the scenario — the seam Phase 2 swaps Zara behind.
public sealed class StubFeeServiceTests
{
    private static readonly FeeScenario Scenario = new(
        new FeeLoanSection("purchase", "conventional", 400000m, 360),
        new FeePropertySection("TX", "78701", "Travis", 500000m, 500000m),
        new FeeBorrowerSection(false, false, false));

    private readonly StubFeeService _svc = new();

    [Fact]
    public async Task Evaluate_returns_deterministic_items_across_sections()
    {
        var eval = await _svc.EvaluateAsync(Scenario, new FeeRequestOptions());

        eval.Adapter.ShouldBe("stub");
        eval.Items.Select(i => i.Section).ShouldBe(["A", "C", "B", "E"]);
        eval.Items.Select(i => i.FeeCode).ShouldBe(
            ["origination", "appraisal", "credit-report", "transfer-tax"]);
        eval.Fingerprint.ShouldBe("stub:v1");
        eval.EvalId.ShouldStartWith("eval-");

        var origination = eval.Items.Single(i => i.FeeCode == "origination");
        origination.Base.ShouldBe(900m);
        origination.Total.ShouldBe(900m);
        origination.Adjustments.Single().RunningTotal.ShouldBe(900m);
    }

    [Fact]
    public async Task Evaluate_derives_transfer_tax_from_loan_amount()
    {
        var eval = await _svc.EvaluateAsync(Scenario, new FeeRequestOptions());
        var tax = eval.Items.Single(i => i.FeeCode == "transfer-tax");
        // 0.1% of the 400k loan amount — amount-driven, not static.
        tax.Total.ShouldBe(400m);
        tax.Adjustments.Single().Value.ShouldBe(0.001m);
    }

    [Fact]
    public async Task Evaluate_fee_codes_filter_limits_items()
    {
        var eval = await _svc.EvaluateAsync(
            Scenario, new FeeRequestOptions(["origination", "credit-report"]));

        eval.Items.Select(i => i.FeeCode)
            .ShouldBe(["origination", "credit-report"]);
    }
}
