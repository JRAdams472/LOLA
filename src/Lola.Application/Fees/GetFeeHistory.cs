using Lola.Application.Admin;
using Lola.Application.Applications;

namespace Lola.Application.Fees;

/// GET …/applications/{id}/fees/history (MPE-183) — every eval for
/// the application oldest-first; the append-only `fee_evals` table
/// is the spine behind this endpoint.
public sealed record GetFeeHistoryQuery(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower);

public static class GetFeeHistoryHandler
{
    public static async Task<IReadOnlyList<FeeEvalDto>> Handle(
        GetFeeHistoryQuery q, IApplicationRepository apps,
        IFeeRepository fees, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.ActorIsBorrower, ct);
        var rows = await fees.ListEvalsAsync(q.AppId, ct);
        return rows.Select(FeeWire.ToDto).ToList();
    }
}
