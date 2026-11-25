using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Identity;

namespace Lola.Application.Compliance;

/// GET …/applications/{id}/atr (M22 P2) — the evaluation posture:
/// latest battery verdicts + figures, plus the append-only lineage
/// of prior evaluations. Read-only; staff-only.
public sealed record GetApplicationAtrQuery(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class GetApplicationAtrHandler
{
    public static async Task<AtrQmReportDto> Handle(
        GetApplicationAtrQuery q, IApplicationRepository apps,
        IAtrQmRepository atr, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "atr/qm posture is staff-facing");
        await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, ActorScope.Staff, ct);

        var rows = await atr.ListResultsAsync(q.AppId, ct);
        var all = rows.Select(r => new AtrEvaluationDto(
            r.ResultId, r.EvalId, r.Dti, r.Apr, r.Apor,
            AtrQmEvaluator.ParseVerdicts(r.VerdictsJson),
            r.Actor, r.CreatedUtc)).ToList();
        return new AtrQmReportDto(all.Count == 0 ? null : all[0], all);
    }
}
