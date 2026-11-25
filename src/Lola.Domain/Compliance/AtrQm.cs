namespace Lola.Domain.Compliance;

/// ATR/QM + high-cost test battery (M22 P1, MPE-306) — the statutory
/// rate-and-fee determinations over application + fee state:
///
///   atr_general      — §1026.43(c) ability-to-repay: the engine never
///                      auto-passes a loan it can't compute; DTI > 43%
///                      is warn (non-QM ATR is legal — human judgment),
///                      obligations ≥ income is fail.
///   qm_general       — §1026.43(e): term ≤ 360, no IO / neg-am /
///                      balloon features, points-and-fees ≤ cap,
///                      DTI ≤ 43.
///   qm_hud           — 24 CFR 203.19 (FHA QM): feature tests + P&F cap;
///                      not applicable to non-FHA product types.
///   hoepa            — §1026.32: APR−APOR spread (6.5pp first lien /
///                      8.5pp subordinate-or-small) or P&F > 5%
///                      (8% under the small-loan floor).
///   hpml             — §1026.35: APR−APOR > 1.5pp first-lien
///                      conforming, > 2.5pp jumbo, > 3.5pp subordinate.
///   state_high_cost  — the seeded per-state trigger row.
///
/// Missing input → warn (`needs review`), never a silent pass. Unknown
/// fee codes fail closed to 'counted' in the P&F aggregate — the caller
/// resolves `pf_classes` before constructing lines.
public static class AtrQm
{
    /// §1026.43(e) / §1026.32 / §1026.35 / 24 CFR 203.19 constants —
    /// base (pre-indexing) statutory amounts with citations; indexed
    /// thresholds are an ops concern when CFPB publishes them.
    public const decimal QmMaxDti = 43m;               // §1026.43(e)(2)(vi)(B)
    public const int QmMaxTermMonths = 360;            // §1026.43(e)(2)(iv)
    public const decimal HoepaFirstLienSpread = 6.5m;  // §1026.32(a)(1)(i)(A)
    public const decimal HoepaSmallOrSubSpread = 8.5m; // §1026.32(a)(1)(i)(B)
    public const decimal HoepaPfPct = 5m;              // §1026.32(a)(1)(ii)(A)
    public const decimal HoepaPfPctSmall = 8m;         // §1026.32(a)(1)(ii)(B)
    public const decimal HoepaSmallLoanFloor = 20_000m;
    public const decimal HoepaSmallFirstLien = 50_000m;
    public const decimal HpmlFirstLienSpread = 1.5m;   // §1026.35(a)(1)(i)
    public const decimal HpmlJumboSpread = 2.5m;       // §1026.35(a)(1)(ii)
    public const decimal HpmlSubordinateSpread = 3.5m; // §1026.35(a)(1)
    public const decimal ConformingLimit = 766_550m;   // FHFA base limit
    public const decimal HudQmPfPct = 3m;              // 24 CFR 203.19(d)

    /// One fee line resolved to its `pf_classes` category:
    /// 'counted' | 'excluded' | 'discount_point' (counts unless bona
    /// fide — unverifiable, so it counts). Callers fail closed: a fee
    /// code with no registry row lands here as 'counted'.
    public sealed record PfLine(string FeeCode, decimal Amount,
        string Category);

    /// The seeded `state_high_cost_rules` row for the property state;
    /// null = no state trigger.
    public sealed record StateRule(string StateCode, decimal RateSpread,
        decimal PfPct, decimal PfFloor);

    /// The battery's input — resolved by the caller from the app doc,
    /// latest fee eval, and the registry/APOR/state tables.
    public sealed record Input(
        decimal? LoanAmount,
        decimal? NoteRatePct,
        int? TermMonths,
        string? AmortizationType,
        string? LienPosition,
        string? ProductType,
        decimal? MonthlyIncome,
        decimal? MonthlyDebts,
        decimal? Apor,
        IReadOnlyList<PfLine>? PfLines,
        StateRule? State);

    public sealed record Result(string Test, AtrVerdict Verdict,
        string Detail);

    /// Points-and-fees aggregate — everything not 'excluded' counts
    /// (fail closed).
    public static decimal PointsAndFees(IReadOnlyList<PfLine>? lines) =>
        lines?.Where(l => l.Category != "excluded")
              .Sum(l => l.Amount) ?? 0m;

    /// §1026.43(e)(3) points-and-fees cap schedule (base amounts —
    /// indexed annually by the CFPB).
    public static decimal QmPfCap(decimal loanAmount) => loanAmount switch
    {
        >= 100_000m => loanAmount * 0.03m,
        >= 60_000m => 3_000m,
        >= 20_000m => loanAmount * 0.05m,
        >= 12_500m => 1_000m,
        _ => loanAmount * 0.08m,
    };

    /// The solved Appendix-J APR for this input — prepaid finance
    /// charge approximated by the points-and-fees aggregate.
    public static decimal? Apr(Input i) =>
        i.LoanAmount is > 0 && i.NoteRatePct is >= 0 && i.TermMonths is > 0
            ? LoanMath.Apr(i.LoanAmount.Value, PointsAndFees(i.PfLines),
                i.NoteRatePct.Value, i.TermMonths.Value, i.AmortizationType)
            : null;

    /// Back-end DTI % — (monthly debts + proposed payment) / income.
    public static decimal? Dti(Input i)
    {
        if (i.MonthlyIncome is not > 0 || i.MonthlyDebts is null
            || i.LoanAmount is not > 0 || i.NoteRatePct is null
            || i.TermMonths is not > 0)
            return null;
        var pmt = LoanMath.ProposedPayment(i.LoanAmount.Value,
            i.NoteRatePct.Value, i.TermMonths.Value, i.AmortizationType);
        return Math.Round((i.MonthlyDebts.Value + pmt)
            / i.MonthlyIncome.Value * 100m, 4);
    }

    /// Proposed monthly payment as exposed for the report surface.
    public static decimal? ProposedPayment(Input i) =>
        i.LoanAmount is > 0 && i.NoteRatePct is >= 0 && i.TermMonths is > 0
            ? LoanMath.ProposedPayment(i.LoanAmount.Value,
                i.NoteRatePct.Value, i.TermMonths.Value, i.AmortizationType)
            : null;

    public static IReadOnlyList<Result> Run(Input i) =>
    [
        AtrGeneral(i), QmGeneral(i), QmHud(i),
        Hoepa(i), Hpml(i), StateHighCost(i),
    ];

    /// §1026.43(c) — the engine never declares a loan un-repayable on
    /// data alone below the obligations≥income bound; DTI > 43% is a
    /// manual-underwriting warn, not a fail.
    public static Result AtrGeneral(Input i)
    {
        var dti = Dti(i);
        if (dti is null)
            return new("atr_general", AtrVerdict.Warn,
                "missing income/debt/payment inputs — needs review");
        if (dti >= 100m)
            return new("atr_general", AtrVerdict.Fail,
                $"obligations ≥ income (dti={dti:0.00})");
        return dti <= QmMaxDti
            ? new("atr_general", AtrVerdict.Pass, $"dti={dti:0.00}")
            : new("atr_general", AtrVerdict.Warn,
                $"dti={dti:0.00}>43 — manual underwriting required");
    }

    /// §1026.43(e) General QM — feature prohibitions + P&F cap + DTI.
    public static Result QmGeneral(Input i)
    {
        var issues = new List<string>();
        var missing = new List<string>();

        if (i.TermMonths is null) missing.Add("term");
        else if (i.TermMonths > QmMaxTermMonths)
            issues.Add($"term={i.TermMonths}>360");

        if (i.AmortizationType is "interest_only" or "graduated")
            issues.Add($"prohibited feature: {i.AmortizationType}");
        else if (i.AmortizationType is null) missing.Add("amortization");

        var dti = Dti(i);
        if (dti is null) missing.Add("dti inputs");
        else if (dti > QmMaxDti) issues.Add($"dti={dti:0.00}>43");

        if (i.PfLines is null) missing.Add("fee eval");
        else if (i.LoanAmount is > 0)
        {
            var pf = PointsAndFees(i.PfLines);
            var cap = QmPfCap(i.LoanAmount.Value);
            if (pf > cap) issues.Add($"p&f={pf:0.00}>cap {cap:0.00}");
        }
        else missing.Add("loan amount");

        if (issues.Count > 0)
            return new("qm_general", AtrVerdict.Fail,
                string.Join("; ", issues));
        if (missing.Count > 0)
            return new("qm_general", AtrVerdict.Warn,
                $"missing {string.Join('/', missing)} — needs review");
        return new("qm_general", AtrVerdict.Pass,
            $"dti={dti:0.00} p&f within cap");
    }

    /// 24 CFR 203.19 — FHA QM; not applicable to non-FHA products.
    public static Result QmHud(Input i)
    {
        if (i.ProductType != "fha")
            return new("qm_hud", AtrVerdict.Pass, "not applicable");
        var issues = new List<string>();
        var missing = new List<string>();
        if (i.TermMonths is null) missing.Add("term");
        else if (i.TermMonths > QmMaxTermMonths)
            issues.Add($"term={i.TermMonths}>360");
        if (i.AmortizationType is "interest_only" or "graduated")
            issues.Add($"prohibited feature: {i.AmortizationType}");
        if (i.PfLines is null) missing.Add("fee eval");
        else if (i.LoanAmount is > 0)
        {
            var pf = PointsAndFees(i.PfLines);
            var cap = Math.Round(i.LoanAmount.Value * HudQmPfPct / 100m, 2);
            if (pf > cap) issues.Add($"p&f={pf:0.00}>3% {cap:0.00}");
        }
        else missing.Add("loan amount");
        if (issues.Count > 0)
            return new("qm_hud", AtrVerdict.Fail, string.Join("; ", issues));
        if (missing.Count > 0)
            return new("qm_hud", AtrVerdict.Warn,
                $"missing {string.Join('/', missing)} — needs review");
        return new("qm_hud", AtrVerdict.Pass, "fha qm within tests");
    }

    /// §1026.32 — high-cost mortgage trigger (rate spread or P&F).
    public static Result Hoepa(Input i)
    {
        var apr = Apr(i);
        var pf = i.PfLines is null ? (decimal?)null : PointsAndFees(i.PfLines);
        var missing = new List<string>();
        if (apr is null) missing.Add("apr inputs");
        if (i.Apor is null) missing.Add("apor");
        if (pf is null) missing.Add("fee eval");
        if (i.LoanAmount is not > 0) missing.Add("loan amount");
        if (missing.Count > 0)
            return new("hoepa", AtrVerdict.Warn,
                $"missing {string.Join('/', missing)} — needs review");

        var amount = i.LoanAmount!.Value;
        var subordinateOrSmall =
            i.LienPosition is "second" or "subordinate"
            || amount < HoepaSmallFirstLien;
        var spreadLimit = subordinateOrSmall
            ? HoepaSmallOrSubSpread : HoepaFirstLienSpread;
        var spread = apr!.Value - i.Apor!.Value;
        var pfCap = amount < HoepaSmallLoanFloor
            ? amount * HoepaPfPctSmall / 100m
            : amount * HoepaPfPct / 100m;
        var issues = new List<string>();
        if (spread > spreadLimit)
            issues.Add($"apr-apor={spread:0.00}>{spreadLimit}");
        if (pf!.Value > pfCap)
            issues.Add($"p&f={pf.Value:0.00}>{pfCap:0.00}");
        return issues.Count > 0
            ? new("hoepa", AtrVerdict.Fail,
                $"high-cost: {string.Join("; ", issues)}")
            : new("hoepa", AtrVerdict.Pass,
                $"spread={spread:0.00}≤{spreadLimit} p&f={pf.Value:0.00}≤{pfCap:0.00}");
    }

    /// §1026.35 — higher-priced mortgage loan (escrow/appraisal reqs).
    public static Result Hpml(Input i)
    {
        var apr = Apr(i);
        if (apr is null || i.Apor is null)
            return new("hpml", AtrVerdict.Warn,
                "missing apr/apor — needs review");
        var tier = i.LienPosition is "second" or "subordinate"
            ? HpmlSubordinateSpread
            : (i.ProductType == "jumbo" || i.LoanAmount > ConformingLimit)
                ? HpmlJumboSpread : HpmlFirstLienSpread;
        var spread = apr.Value - i.Apor.Value;
        return spread > tier
            ? new("hpml", AtrVerdict.Fail,
                $"apr-apor={spread:0.00}>{tier} — HPML")
            : new("hpml", AtrVerdict.Pass, $"apr-apor={spread:0.00}≤{tier}");
    }

    /// Per-state high-cost trigger — no seeded row means no state rule.
    public static Result StateHighCost(Input i)
    {
        if (i.State is null)
            return new("state_high_cost", AtrVerdict.Pass, "no state rule");
        var apr = Apr(i);
        var pf = i.PfLines is null ? (decimal?)null : PointsAndFees(i.PfLines);
        if (apr is null || i.Apor is null || pf is null
            || i.LoanAmount is not > 0)
            return new("state_high_cost", AtrVerdict.Warn,
                "missing apr/apor/p&f inputs — needs review");
        if (i.LoanAmount.Value < i.State.PfFloor)
            return new("state_high_cost", AtrVerdict.Pass,
                $"below {i.State.StateCode} floor {i.State.PfFloor:0}");
        var spread = apr.Value - i.Apor.Value;
        var pfPct = pf.Value / i.LoanAmount.Value * 100m;
        var issues = new List<string>();
        if (spread > i.State.RateSpread)
            issues.Add($"spread={spread:0.00}>{i.State.RateSpread}");
        if (pfPct > i.State.PfPct)
            issues.Add($"p&f={pfPct:0.00}%>{i.State.PfPct}%");
        return issues.Count > 0
            ? new("state_high_cost", AtrVerdict.Fail,
                $"{i.State.StateCode}: {string.Join("; ", issues)}")
            : new("state_high_cost", AtrVerdict.Pass,
                $"{i.State.StateCode}: spread={spread:0.00} p&f={pfPct:0.00}%");
    }
}

public enum AtrVerdict { Pass, Warn, Fail }
