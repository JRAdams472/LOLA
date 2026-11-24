using Lola.Application.Errors;
using Lola.Application.Fees;

namespace Lola.Infrastructure.Fees;

/// Fail-closed IFeeService for FEE_ADAPTER=disabled (MPE-277): the
/// capability is explicitly off — every call surfaces a 503 rather
/// than simulated fee results.
public sealed class DisabledFeeService : IFeeService
{
    public string Adapter => "disabled";

    public Task<FeeEvaluation> EvaluateAsync(
        FeeScenario scenario, FeeRequestOptions options,
        CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "fee capability is disabled by configuration");
}
