using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Application.Identity;

namespace Lola.Application.Compliance;

/// POST …/applications/{id}/atr/evaluate (M22 P2) — runs the ATR/QM
/// + high-cost battery over the current application state and records
/// the outcome as ONE append-only `atr_results` row plus ONE
/// hash-chained `atr_qm_result` loan event. Re-evaluation is a new
/// row, never an update — the lineage is the audit trail.
public sealed record EvaluateAtrQmCommand(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class EvaluateAtrQmHandler
{
    public static async Task<AtrEvaluationDto> Handle(
        EvaluateAtrQmCommand c, IApplicationRepository apps,
        IAtrQmRepository atr, Fees.IFeeRepository fees,
        ILoanEventLog events, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "atr/qm evaluation is a staff action");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, ActorScope.Staff, ct);

        // Latest COMPLETED eval feeds the points-and-fees lines; absent
        // one, the battery still runs — P&F-dependent tests warn.
        var evals = await fees.ListEvalsAsync(c.AppId, ct);
        var eval = evals.FirstOrDefault(e => e.Status == "done");
        var items = eval is null
            ? null : await fees.ListItemsAsync(eval.EvalId, ct);
        var overrides = await fees.ListOverridesAsync(c.AppId, ct);

        var pfClasses = await atr.GetPfClassesAsync(ct);
        var apor = await atr.GetAporAsync(ct);
        var stateCode = app.Doc.Get("subject_property.state")
            ?.GetValue<string>();
        var state = stateCode is null
            ? null : await atr.GetStateRuleAsync(stateCode, ct);

        var input = AtrQmEvaluator.Map(app.Doc, eval is not null, items,
            overrides, pfClasses, apor, state, eval?.Variant ?? "");
        var results = Domain.Compliance.AtrQm.Run(input);
        var dti = Domain.Compliance.AtrQm.Dti(input);
        var apr = Domain.Compliance.AtrQm.Apr(input);

        var record = await atr.AddResultAsync(c.AppId, eval?.EvalId,
            dti, apr, apor is null ? null : apor.Apor,
            AtrQmEvaluator.VerdictsJson(results), c.ActorId, ct);

        var summary = string.Join("; ", results.Select(r =>
            $"{r.Test}:{r.Verdict.ToString().ToLowerInvariant()}"));
        await events.WriteAsync(new LoanEventDraft(c.AppId,
            LoanEventKind.AtrQmResult, c.ActorId,
            "atr_result", record.ResultId, "compliance", null,
            null, summary), ct);

        return new AtrEvaluationDto(record.ResultId, record.EvalId,
            record.Dti, record.Apr, record.Apor,
            results.Select(r => new AtrVerdictDto(r.Test,
                r.Verdict.ToString().ToLowerInvariant(), r.Detail))
                .ToList(),
            record.Actor, record.CreatedUtc);
    }
}
