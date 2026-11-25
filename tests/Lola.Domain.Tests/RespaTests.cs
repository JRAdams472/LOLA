using Lola.Domain.Compliance;
using Shouldly;
using Xunit;

namespace Lola.Domain.Tests;

/// M23 P1 fixture proofs — §1024.17 escrow analysis + §1024.33
/// servicing-transfer deadlines. Every statutory boundary proven on
/// both sides.
public class EscrowAnalysisTests
{
    // Canonical fixture: $3,600 tax + $1,200 insurance → $4,800/yr,
    // $400/mo disbursement, 2-month cushion target $800.
    private static EscrowAnalysis.Input Fixture(
        decimal? balance = 800m, decimal? payment = 400m,
        decimal? tax = 3600m, decimal? ins = 1200m,
        decimal? pmi = null, decimal? hoa = null, int? cushion = null)
        => new(balance, payment, tax, ins, pmi, hoa, cushion);

    [Fact]
    public void Balanced_account_at_cushion_is_within()
    {
        var r = EscrowAnalysis.Analyze(Fixture());
        r.Outcome.ShouldBe(EscrowAnalysis.Verdict.Within);
        r.AnnualDisbursements.ShouldBe(4800m);
        r.CushionTarget.ShouldBe(800m);
        r.LowPoint.ShouldBe(800m);
        r.DeterminationAmount.ShouldBe(0m);
        r.RefundRequired.ShouldBeFalse();
    }

    [Fact]
    public void Balance_below_cushion_is_shortage_by_the_gap()
    {
        var r = EscrowAnalysis.Analyze(Fixture(balance: 500m));
        r.Outcome.ShouldBe(EscrowAnalysis.Verdict.Shortage);
        r.DeterminationAmount.ShouldBe(300m); // 800 − 500
        r.RefundRequired.ShouldBeFalse();
    }

    [Fact]
    public void Surplus_of_50_or_more_sets_the_refund_clock()
    {
        var exact = EscrowAnalysis.Analyze(Fixture(balance: 850m));
        exact.Outcome.ShouldBe(EscrowAnalysis.Verdict.Surplus);
        exact.DeterminationAmount.ShouldBe(-50m);
        exact.RefundRequired.ShouldBeTrue(); // ≥ $50 — boundary ON

        var over = EscrowAnalysis.Analyze(Fixture(balance: 1300m));
        over.DeterminationAmount.ShouldBe(-500m);
        over.RefundRequired.ShouldBeTrue();
    }

    [Fact]
    public void Surplus_under_50_does_not_set_the_refund_clock()
    {
        var r = EscrowAnalysis.Analyze(Fixture(balance: 849.99m));
        r.Outcome.ShouldBe(EscrowAnalysis.Verdict.Surplus);
        r.DeterminationAmount.ShouldBe(-49.99m);
        r.RefundRequired.ShouldBeFalse(); // < $50 — boundary OFF
    }

    [Fact]
    public void Negative_low_point_is_deficient()
    {
        var r = EscrowAnalysis.Analyze(Fixture(balance: -100m));
        r.Outcome.ShouldBe(EscrowAnalysis.Verdict.Deficient);
        // Distance still measures the full cure: 800 − (−100).
        r.DeterminationAmount.ShouldBe(900m);
    }

    [Fact]
    public void Cushion_election_above_two_months_is_capped()
    {
        var r = EscrowAnalysis.Analyze(Fixture(cushion: 4));
        r.CushionMonthsApplied.ShouldBe(2);   // statutory cap wins
        r.CushionTarget.ShouldBe(800m);       // 2 × 400, not 4 × 400
    }

    [Fact]
    public void Low_point_follows_net_flow_sign()
    {
        // Payment > disbursement → balance climbs → low point is now.
        var climbing = EscrowAnalysis.Analyze(Fixture(payment: 500m));
        climbing.LowPoint.ShouldBe(800m);
        // Payment < disbursement → balance falls → low point month 12:
        // 800 + 12 × (300 − 400) = −400.
        var falling = EscrowAnalysis.Analyze(Fixture(payment: 300m));
        falling.LowPoint.ShouldBe(-400m);
        falling.Outcome.ShouldBe(EscrowAnalysis.Verdict.Deficient);
    }

    [Fact]
    public void Pmi_and_hoa_join_the_disbursement_total()
    {
        var r = EscrowAnalysis.Analyze(
            Fixture(balance: 900m, payment: 500m, pmi: 600m, hoa: 600m));
        r.AnnualDisbursements.ShouldBe(6000m);
        r.CushionTarget.ShouldBe(1000m); // 2 × 500
        r.Outcome.ShouldBe(EscrowAnalysis.Verdict.Shortage);
    }

    [Fact]
    public void Missing_payment_is_incomplete_never_a_verdict()
    {
        var r = EscrowAnalysis.Analyze(Fixture(payment: null));
        r.Outcome.ShouldBe(EscrowAnalysis.Verdict.Incomplete);
        r.MissingInputs.ShouldContain("escrow.monthly_payment");
        r.DeterminationAmount.ShouldBeNull();
    }

    [Fact]
    public void Missing_balance_is_incomplete()
    {
        var r = EscrowAnalysis.Analyze(Fixture(balance: null));
        r.Outcome.ShouldBe(EscrowAnalysis.Verdict.Incomplete);
        r.MissingInputs.ShouldContain("escrow.current_balance");
    }

    [Fact]
    public void Zero_annual_disbursements_is_incomplete_not_division_error()
    {
        var r = EscrowAnalysis.Analyze(Fixture(tax: 0m, ins: 0m));
        r.Outcome.ShouldBe(EscrowAnalysis.Verdict.Incomplete);
        r.MissingInputs.ShouldContain("escrow.annual_* (at least one)");
    }

    [Fact]
    public void One_month_cushion_election_is_honored()
    {
        var r = EscrowAnalysis.Analyze(Fixture(cushion: 1, balance: 400m));
        r.CushionMonthsApplied.ShouldBe(1);
        r.CushionTarget.ShouldBe(400m);
        r.Outcome.ShouldBe(EscrowAnalysis.Verdict.Within);
    }
}

public class ServicingTransferTests
{
    private static readonly DateOnly Effective = new(2025, 3, 20);

    [Fact]
    public void Transferor_deadline_is_15_days_before_effective()
        => ServicingTransfer.SendBy(ServicingTransfer.Kind.Transferor, Effective)
            .ShouldBe(new DateOnly(2025, 3, 5));

    [Fact]
    public void Transferee_deadline_is_15_days_after_effective()
        => ServicingTransfer.SendBy(ServicingTransfer.Kind.Transferee, Effective)
            .ShouldBe(new DateOnly(2025, 4, 4));

    [Fact]
    public void Sending_on_the_deadline_day_is_timely()
    {
        var sendBy = new DateOnly(2025, 3, 5);
        ServicingTransfer.Overdue(sendBy, null, sendBy).ShouldBeFalse();
        ServicingTransfer.Overdue(sendBy, null,
            sendBy.AddDays(1)).ShouldBeTrue(); // the day AFTER is breach
    }

    [Fact]
    public void Sent_notice_is_never_overdue()
    {
        var sendBy = new DateOnly(2025, 3, 5);
        ServicingTransfer.Overdue(sendBy, new DateOnly(2025, 3, 1),
            new DateOnly(2025, 4, 1)).ShouldBeFalse();
    }

    [Fact]
    public void Sent_late_detects_the_violation_at_the_boundary()
    {
        var sendBy = new DateOnly(2025, 3, 5);
        ServicingTransfer.SentLate(sendBy, sendBy).ShouldBeFalse();
        ServicingTransfer.SentLate(sendBy,
            sendBy.AddDays(1)).ShouldBeTrue();
    }
}
