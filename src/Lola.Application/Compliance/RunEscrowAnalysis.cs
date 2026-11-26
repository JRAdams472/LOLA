using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Application.Identity;

namespace Lola.Application.Compliance;

/// POST …/applications/{id}/respa/escrow/analyze (M23 P2) — runs the
/// §1024.17 escrow analysis over the application's escrow inputs and
/// records the outcome as ONE append-only `escrow_analyses` row plus
/// ONE hash-chained `escrow_analysis` loan event (verdict summary
/// only — never figures, invariant 3). Re-analysis is a new row,
/// never an update — the lineage is the audit trail.
public sealed record RunEscrowAnalysisCommand(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class RunEscrowAnalysisHandler
{
    public static async Task<EscrowAnalysisDto> Handle(
        RunEscrowAnalysisCommand c, IApplicationRepository apps,
        IRespaRepository respa, ILoanEventLog events,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "escrow analysis is a staff action");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, ActorScope.Staff, ct);

        var input = RespaEvaluator.MapEscrow(app.Doc);
        var result = Domain.Compliance.EscrowAnalysis.Analyze(input);

        var inputsJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            current_balance = input.CurrentBalance,
            monthly_payment = input.MonthlyPayment,
            annual_property_tax = input.AnnualPropertyTax,
            annual_hazard_insurance = input.AnnualHazardInsurance,
            annual_pmi = input.AnnualPmi,
            annual_hoa = input.AnnualHoa,
            cushion_months = input.CushionMonths,
        });
        var detailJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            missing_inputs = result.MissingInputs,
            cushion_months_applied = result.CushionMonthsApplied,
        });

        var record = await respa.AddAnalysisAsync(c.AppId,
            result.Outcome.ToString().ToLowerInvariant(),
            result.AnnualDisbursements, result.CushionTarget,
            result.LowPoint, result.DeterminationAmount,
            result.RefundRequired, inputsJson, detailJson,
            c.ActorId, ct);

        await events.WriteAsync(new LoanEventDraft(c.AppId,
            LoanEventKind.EscrowAnalysis, c.ActorId,
            "escrow_analysis", record.AnalysisId, "compliance", null,
            null,
            $"verdict:{result.Outcome.ToString().ToLowerInvariant()}" +
            (result.RefundRequired ? ";refund_clock" : "")), ct);

        return ToDto(record, result.MissingInputs);
    }

    internal static EscrowAnalysisDto ToDto(EscrowAnalysisRecord r,
        IReadOnlyList<string>? missing = null)
        => new(r.AnalysisId, r.Verdict,
            (decimal?)r.AnnualDisbursements, (decimal?)r.CushionTarget,
            (decimal?)r.LowPoint, (decimal?)r.DeterminationAmount,
            r.RefundRequired != 0,
            missing ?? ParseMissing(r.DetailJson),
            r.Actor, r.CreatedUtc);

    internal static IReadOnlyList<string> ParseMissing(string detailJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(detailJson);
            if (!doc.RootElement.TryGetProperty("missing_inputs",
                    out var arr))
                return [];
            return arr.EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(s => s.Length > 0)
                .ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
