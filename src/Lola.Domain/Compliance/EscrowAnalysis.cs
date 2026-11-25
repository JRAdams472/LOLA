namespace Lola.Domain.Compliance;

/// Reg X §1024.17 — escrow account analysis. Compliance posture only:
/// given the account's current state, project the 12-month trial
/// balance and determine whether the account is within cushion,
/// short, in surplus, or deficient. Missing inputs yield `incomplete`
/// — never a silent determination.
///
/// Model: disbursements spread evenly across the projection year
/// (annual/12 per month). With even spread the trial balance is
/// linear in the payment-minus-disbursement delta, so the low point
/// is month 0 or month 12 — the statutory determinations (cushion
/// cap, shortage/surplus magnitude, deficiency) are exact under this
/// projection. The cushion target is capped at 1/6 of annual
/// disbursements (2 months) regardless of the note's election —
/// §1024.17(c)(1)(i).
public static class EscrowAnalysis
{
    public enum Verdict { Within, Shortage, Surplus, Deficient, Incomplete }

    public sealed record Input(
        decimal? CurrentBalance,
        decimal? MonthlyPayment,
        decimal? AnnualPropertyTax,
        decimal? AnnualHazardInsurance,
        decimal? AnnualPmi,
        decimal? AnnualHoa,
        int? CushionMonths);

    public sealed record Result(
        Verdict Outcome,
        decimal? AnnualDisbursements,
        decimal? CushionTarget,
        decimal? LowPoint,
        /// Shortage magnitude when positive, surplus magnitude when
        /// negative — the dollar distance from the low point to the
        /// cushion target.
        decimal? DeterminationAmount,
        /// §1024.17(i): a surplus of $50 or more must be refunded
        /// within 30 days.
        bool RefundRequired,
        int CushionMonthsApplied,
        IReadOnlyList<string> MissingInputs);

    public static Result Analyze(Input i)
    {
        var missing = new List<string>();
        if (i.CurrentBalance is null) missing.Add("escrow.current_balance");
        if (i.MonthlyPayment is null) missing.Add("escrow.monthly_payment");
        var anyAnnual = (i.AnnualPropertyTax ?? 0m) + (i.AnnualHazardInsurance ?? 0m)
            + (i.AnnualPmi ?? 0m) + (i.AnnualHoa ?? 0m);
        if (anyAnnual <= 0m) missing.Add("escrow.annual_* (at least one)");

        if (missing.Count > 0)
            return new Result(Verdict.Incomplete, null, null, null, null,
                false, 0, missing);

        var annual = anyAnnual;
        var monthlyDisbursement = annual / 12m;
        var elected = Math.Min(i.CushionMonths ?? 2, 2); // statutory cap
        var cushionTarget = Math.Round(monthlyDisbursement * elected, 2);

        // Even-spread trial balance: B(m) = B0 + m·(P − D), m ∈ [0,12].
        // The low point is at an endpoint — month 0 when the net flow
        // is non-negative, month 12 when negative.
        var net = i.MonthlyPayment!.Value - monthlyDisbursement;
        var b0 = i.CurrentBalance!.Value;
        var b12 = b0 + 12m * net;
        var lowPoint = Math.Round(Math.Min(b0, b12), 2);

        // Determination vs. cushion target — positive = shortage,
        // negative = surplus. Zero is exactly within.
        var distance = Math.Round(cushionTarget - lowPoint, 2);
        var deficient = lowPoint < 0m;
        var outcome = deficient ? Verdict.Deficient
            : distance > 0m ? Verdict.Shortage
            : distance < 0m ? Verdict.Surplus
            : Verdict.Within;
        var refund = outcome == Verdict.Surplus && -distance >= 50m;

        return new Result(outcome, annual, cushionTarget, lowPoint,
            distance, refund, elected, []);
    }
}
