using Lola.Application.Fees;

namespace Lola.Infrastructure.Fees;

/// Default fee provider (MPE-75): a deterministic fixed item set
/// spanning LE sections A/B/C/E so the eval surface, outbox, and
/// override flow are exercisable without Zara. `FEE_ADAPTER=zara`
/// swaps the real adapter in behind the same interface.
public sealed class StubFeeService : IFeeService
{
    public string Adapter => "stub";

    public Task<FeeEvaluation> EvaluateAsync(
        FeeScenario scenario, FeeRequestOptions options,
        CancellationToken ct = default)
    {
        var amount = scenario.Loan.Amount ?? 0m;
        IReadOnlyList<FeeLine> items =
        [
            new("origination", "Origination Fee", "A", 900m,
                [new("base", "set", 900m, 900m)], 900m),
            new("appraisal", "Appraisal Fee", "C", 650m,
                [new("base", "set", 650m, 650m)], 650m),
            new("credit-report", "Credit Report", "B", 45m,
                [new("base", "set", 45m, 45m)], 45m),
            new("transfer-tax", "Transfer Tax", "E", 0m,
                [new("rate", "mul", 0.001m, Math.Round(amount * 0.001m, 2))],
                Math.Round(amount * 0.001m, 2)),
        ];
        if (options.FeeCodes is { } codes)
            items = items.Where(i => codes.Contains(i.FeeCode)).ToList();
        return Task.FromResult(new FeeEvaluation(
            $"eval-{Guid.NewGuid():N}", Adapter,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            "stub:v1", items));
    }
}
