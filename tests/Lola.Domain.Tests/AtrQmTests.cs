using Lola.Domain.Compliance;
using Shouldly;
using Xunit;

namespace Lola.Domain.Tests;

public sealed class AtrQmTests
{
    // -- LoanMath --------------------------------------------------

    [Fact]
    public void Payment_fixed_rate_matches_amortization_schedule()
    {
        LoanMath.Payment(300_000m, 6.5m, 360).ShouldBe(1896.20m);
        LoanMath.Payment(200_000m, 5.0m, 180).ShouldBe(1581.59m);
    }

    [Fact]
    public void Apr_zero_fees_returns_note_rate()
    {
        AtrQm.Apr(new AtrQm.Input(300_000m, 6.5m, 360,
            "fixed", "first", "conventional", 8_000m, 500m, 6.5m,
            [], null))
            .ShouldBe(6.5m);
    }

    [Fact]
    public void Apr_matches_fixture_within_hundredth()
    {
        var fees = new[] { new AtrQm.PfLine("origination", 3_000m, "counted") };
        var apr = AtrQm.Apr(new AtrQm.Input(300_000m, 6.5m, 360,
            "fixed", "first", "conventional", 8_000m, 500m, 6.5m,
            fees, null));
        apr.ShouldNotBeNull();
        apr.Value.ShouldBe(6.5969m, 0.01m);
    }

    [Fact]
    public void Apr_degenerate_inputs_return_null()
    {
        LoanMath.Apr(0m, 0m, 6.5m, 360, "fixed").ShouldBeNull();
        LoanMath.Apr(300_000m, 0m, 6.5m, 0, "fixed").ShouldBeNull();
        LoanMath.Apr(300_000m, 300_000m, 6.5m, 360, "fixed").ShouldBeNull();
    }

    // -- Helpers ---------------------------------------------------

    private static AtrQm.Input BaseInput(
        decimal? amount = 300_000m, decimal? rate = 6.5m,
        int? term = 360, string? amort = "fixed", string? lien = "first",
        string? product = "conventional", decimal? income = 8_000m,
        decimal? debts = 500m, decimal? apor = 6.5m,
        IReadOnlyList<AtrQm.PfLine>? pf = null,
        AtrQm.StateRule? state = null) =>
        new(amount, rate, term, amort, lien, product, income, debts,
            apor, pf ?? [], state);

    private static AtrQm.Result Of(
        IReadOnlyList<AtrQm.Result> results, string test) =>
        results.Single(r => r.Test == test);

    // -- atr_general -----------------------------------------------

    [Fact]
    public void Atr_general_passes_when_dti_within_43()
    {
        var r = Of(AtrQm.Run(BaseInput()), "atr_general");
        r.Verdict.ShouldBe(AtrVerdict.Pass);
        r.Detail.ShouldContain("dti=29.95");
    }

    [Fact]
    public void Atr_general_warns_when_inputs_missing()
    {
        var r = Of(AtrQm.Run(BaseInput(income: null)), "atr_general");
        r.Verdict.ShouldBe(AtrVerdict.Warn);
    }

    [Fact]
    public void Atr_general_warns_on_high_dti_not_fail()
    {
        // $500 debts + $1,896 payment on $2,300 income → dti≈104 → fail.
        var r = Of(AtrQm.Run(BaseInput(income: 4_500m)), "atr_general");
        r.Verdict.ShouldBe(AtrVerdict.Warn);
        r.Detail.ShouldContain("manual underwriting");
    }

    [Fact]
    public void Atr_general_fails_when_obligations_exceed_income()
    {
        var r = Of(AtrQm.Run(BaseInput(income: 2_300m)), "atr_general");
        r.Verdict.ShouldBe(AtrVerdict.Fail);
    }

    // -- qm_general ------------------------------------------------

    [Fact]
    public void Qm_general_passes_a_clean_conforming_loan()
    {
        var pf = new[] { new AtrQm.PfLine("origination", 1_000m, "counted") };
        Of(AtrQm.Run(BaseInput(pf: pf)), "qm_general")
            .Verdict.ShouldBe(AtrVerdict.Pass);
    }

    [Fact]
    public void Qm_general_dti_boundary_43_00_vs_43_01()
    {
        // income tuned so dti lands exactly on each side.
        var pmt = LoanMath.ProposedPayment(300_000m, 6.5m, 360, "fixed");
        var at43 = (500m + pmt) / 0.43m;            // income → dti = 43.00
        var pf = Array.Empty<AtrQm.PfLine>();
        Of(AtrQm.Run(BaseInput(income: at43, pf: pf)), "qm_general")
            .Verdict.ShouldBe(AtrVerdict.Pass);
        Of(AtrQm.Run(BaseInput(income: at43 - 1m, pf: pf)), "qm_general")
            .Verdict.ShouldBe(AtrVerdict.Fail);
    }

    [Fact]
    public void Qm_general_fails_on_term_over_360()
    {
        Of(AtrQm.Run(BaseInput(term: 480)), "qm_general")
            .Verdict.ShouldBe(AtrVerdict.Fail);
    }

    [Fact]
    public void Qm_general_fails_on_prohibited_features()
    {
        Of(AtrQm.Run(BaseInput(amort: "interest_only")), "qm_general")
            .Verdict.ShouldBe(AtrVerdict.Fail);
        Of(AtrQm.Run(BaseInput(amort: "graduated")), "qm_general")
            .Verdict.ShouldBe(AtrVerdict.Fail);
    }

    [Fact]
    public void Qm_general_fails_when_pf_exceeds_cap()
    {
        // 3% of 300k = $9,000 cap.
        var pf = new[] { new AtrQm.PfLine("origination", 9_001m, "counted") };
        var r = Of(AtrQm.Run(BaseInput(pf: pf)), "qm_general");
        r.Verdict.ShouldBe(AtrVerdict.Fail);
        r.Detail.ShouldContain("p&f=9001.00>cap 9000.00");
    }

    [Fact]
    public void Qm_general_pf_cap_schedule_scales_by_amount()
    {
        AtrQm.QmPfCap(150_000m).ShouldBe(4_500m);
        AtrQm.QmPfCap(80_000m).ShouldBe(3_000m);
        AtrQm.QmPfCap(40_000m).ShouldBe(2_000m);
        AtrQm.QmPfCap(15_000m).ShouldBe(1_000m);
        AtrQm.QmPfCap(10_000m).ShouldBe(800m);
    }

    [Fact]
    public void Qm_general_warns_when_fee_eval_absent()
    {
        // Null line set = no completed eval — warn, not pass.
        var input = BaseInput() with { PfLines = null };
        Of(AtrQm.Run(input), "qm_general")
            .Verdict.ShouldBe(AtrVerdict.Warn);
    }

    // -- pf classification -----------------------------------------

    [Fact]
    public void Unknown_fee_code_counts_toward_pf()
    {
        // Caller maps unseeded codes to 'counted' — anything not
        // 'excluded' lands in the aggregate.
        var lines = new[]
        {
            new AtrQm.PfLine("never-seen", 500m, "counted"),
            new AtrQm.PfLine("appraisal", 600m, "excluded"),
            new AtrQm.PfLine("discount-points", 1_000m, "discount_point"),
        };
        AtrQm.PointsAndFees(lines).ShouldBe(1_500m);
    }

    // -- hoepa -------------------------------------------------------

    [Fact]
    public void Hoepa_first_lien_spread_boundary()
    {
        // APR 13.0 vs APOR 6.5 → spread 6.50 = pass; 6.51 = fail.
        // Drive the spread via fee load: APR = 6.5 + ~x with $0 fees.
        var passInput = BaseInput(rate: 13.0m);
        Of(AtrQm.Run(passInput), "hoepa")
            .Verdict.ShouldBe(AtrVerdict.Pass);
        var failInput = BaseInput(rate: 13.01m);
        var r = Of(AtrQm.Run(failInput), "hoepa");
        r.Verdict.ShouldBe(AtrVerdict.Fail);
        r.Detail.ShouldContain("apr-apor=6.51>6.5");
    }

    [Fact]
    public void Hoepa_subordinate_uses_8_5pp_tier()
    {
        // First-lien-spread-breaching rate must still pass subordinate.
        var r = Of(AtrQm.Run(
            BaseInput(rate: 14.0m, lien: "second")), "hoepa");
        r.Verdict.ShouldBe(AtrVerdict.Pass);   // spread 7.50 ≤ 8.5
        var r2 = Of(AtrQm.Run(
            BaseInput(rate: 15.1m, lien: "second")), "hoepa");
        r2.Verdict.ShouldBe(AtrVerdict.Fail);  // spread 8.60 > 8.5
    }

    [Fact]
    public void Hoepa_pf_trigger_at_5_percent()
    {
        // $300k × 5% = $15,000.
        var pf = new[] { new AtrQm.PfLine("origination", 15_001m, "counted") };
        var r = Of(AtrQm.Run(BaseInput(pf: pf)), "hoepa");
        r.Verdict.ShouldBe(AtrVerdict.Fail);
        r.Detail.ShouldContain("high-cost");
    }

    [Fact]
    public void Hoepa_small_loan_uses_8_percent_pf()
    {
        // $15k loan → 8% cap = $1,200.
        var pf = new[] { new AtrQm.PfLine("origination", 1_100m, "counted") };
        Of(AtrQm.Run(BaseInput(amount: 15_000m, pf: pf)), "hoepa")
            .Verdict.ShouldBe(AtrVerdict.Pass);
        var pf2 = new[] { new AtrQm.PfLine("origination", 1_201m, "counted") };
        Of(AtrQm.Run(BaseInput(amount: 15_000m, pf: pf2)), "hoepa")
            .Verdict.ShouldBe(AtrVerdict.Fail);
    }

    [Fact]
    public void Hoepa_warns_when_apor_missing()
    {
        Of(AtrQm.Run(BaseInput(apor: null)), "hoepa")
            .Verdict.ShouldBe(AtrVerdict.Warn);
    }

    // -- hpml -------------------------------------------------------

    [Fact]
    public void Hpml_first_lien_conforming_tier()
    {
        // spread 1.50 passes, 1.51 fails.
        Of(AtrQm.Run(BaseInput(rate: 8.0m)), "hpml")
            .Verdict.ShouldBe(AtrVerdict.Pass);
        Of(AtrQm.Run(BaseInput(rate: 8.01m)), "hpml")
            .Verdict.ShouldBe(AtrVerdict.Fail);
    }

    [Fact]
    public void Hpml_jumbo_uses_2_5pp_tier()
    {
        Of(AtrQm.Run(BaseInput(rate: 9.0m, amount: 900_000m)), "hpml")
            .Verdict.ShouldBe(AtrVerdict.Pass);   // spread 2.50 ≤ 2.5
        Of(AtrQm.Run(BaseInput(rate: 9.01m, amount: 900_000m)), "hpml")
            .Verdict.ShouldBe(AtrVerdict.Fail);  // spread 2.51 > 2.5
    }

    [Fact]
    public void Hpml_subordinate_uses_3_5pp_tier()
    {
        Of(AtrQm.Run(BaseInput(rate: 10.0m, lien: "second")), "hpml")
            .Verdict.ShouldBe(AtrVerdict.Pass);   // spread 3.50 ≤ 3.5
        Of(AtrQm.Run(BaseInput(rate: 10.01m, lien: "second")), "hpml")
            .Verdict.ShouldBe(AtrVerdict.Fail);
    }

    // -- qm_hud -----------------------------------------------------

    [Fact]
    public void Qm_hud_not_applicable_to_non_fha()
    {
        var r = Of(AtrQm.Run(BaseInput()), "qm_hud");
        r.Verdict.ShouldBe(AtrVerdict.Pass);
        r.Detail.ShouldContain("not applicable");
    }

    [Fact]
    public void Qm_hud_applies_3_percent_cap_to_fha()
    {
        var pf = new[] { new AtrQm.PfLine("origination", 9_001m, "counted") };
        Of(AtrQm.Run(BaseInput(product: "fha", pf: pf)), "qm_hud")
            .Verdict.ShouldBe(AtrVerdict.Fail);
        var pf2 = new[] { new AtrQm.PfLine("origination", 8_999m, "counted") };
        Of(AtrQm.Run(BaseInput(product: "fha", pf: pf2)), "qm_hud")
            .Verdict.ShouldBe(AtrVerdict.Pass);
    }

    // -- state_high_cost --------------------------------------------

    [Fact]
    public void State_rule_applies_seeded_thresholds()
    {
        var ny = new AtrQm.StateRule("NY", 6.5m, 5.0m, 50_000m);
        Of(AtrQm.Run(BaseInput(rate: 13.1m, state: ny)), "state_high_cost")
            .Verdict.ShouldBe(AtrVerdict.Fail);
        var pf = new[] { new AtrQm.PfLine("origination", 16_000m, "counted") };
        Of(AtrQm.Run(BaseInput(state: ny, pf: pf)), "state_high_cost")
            .Verdict.ShouldBe(AtrVerdict.Fail);   // p&f 5.33% > 5%
    }

    [Fact]
    public void State_rule_floor_exempts_small_loans()
    {
        var ny = new AtrQm.StateRule("NY", 6.5m, 5.0m, 50_000m);
        var pf = new[] { new AtrQm.PfLine("origination", 2_000m, "counted") };
        var r = Of(AtrQm.Run(BaseInput(amount: 40_000m, state: ny, pf: pf)),
            "state_high_cost");
        r.Verdict.ShouldBe(AtrVerdict.Pass);
        r.Detail.ShouldContain("floor");
    }

    [Fact]
    public void Absent_state_rule_passes()
    {
        var r = Of(AtrQm.Run(BaseInput(state: null)), "state_high_cost");
        r.Verdict.ShouldBe(AtrVerdict.Pass);
        r.Detail.ShouldContain("no state rule");
    }

    // -- battery shape -----------------------------------------------

    [Fact]
    public void Run_emits_all_six_tests()
    {
        AtrQm.Run(BaseInput()).Select(r => r.Test).ShouldBe(
            ["atr_general", "qm_general", "qm_hud", "hoepa", "hpml",
             "state_high_cost"], ignoreOrder: true);
    }
}
