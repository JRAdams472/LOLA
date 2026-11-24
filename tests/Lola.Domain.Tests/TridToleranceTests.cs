using Lola.Domain.Compliance;
using Shouldly;
using Xunit;

namespace Lola.Domain.Tests;

public sealed class TridToleranceTests
{
    private static TridTolerance.ToleranceClass? Cls(
        string bucket, bool shoppable = false)
        => new(bucket, shoppable);

    // -- Classification -------------------------------------------

    [Fact]
    public void Lender_paid_always_zero_regardless_of_registry()
    {
        foreach (var paidTo in new[] { "lender", "affiliate", "broker" })
            TridTolerance.Classify("appraisal", "C", paidTo, true,
                Cls("ten_percent", shoppable: true))
                .ShouldBe(ToleranceBucket.Zero);
    }

    [Fact]
    public void Unknown_fee_code_fails_closed_to_zero()
    {
        TridTolerance.Classify("never-seen", "C", "third_party", true, null)
            .ShouldBe(ToleranceBucket.Zero);
    }

    [Fact]
    public void Registry_zero_bucket_stays_zero()
    {
        TridTolerance.Classify("origination", "A", "third_party", false,
            Cls("zero")).ShouldBe(ToleranceBucket.Zero);
    }

    [Fact]
    public void Shoppable_ten_percent_from_list_is_ten_percent()
    {
        TridTolerance.Classify("appraisal", "C", "third_party", true,
            Cls("ten_percent", shoppable: true))
            .ShouldBe(ToleranceBucket.TenPercent);
    }

    [Fact]
    public void Shoppable_ten_percent_off_list_is_unlimited()
    {
        TridTolerance.Classify("appraisal", "C", "third_party", false,
            Cls("ten_percent", shoppable: true))
            .ShouldBe(ToleranceBucket.Unlimited);
    }

    [Fact]
    public void Non_shoppable_ten_percent_is_ten_percent_regardless()
    {
        // e.g. recording fees — bucket applies without list selection.
        TridTolerance.Classify("recording-fee", "E", "third_party", false,
            Cls("ten_percent", shoppable: false))
            .ShouldBe(ToleranceBucket.TenPercent);
    }

    [Fact]
    public void Registry_unlimited_is_unlimited()
    {
        TridTolerance.Classify("prepaid-interest", "F", "third_party",
            false, Cls("unlimited"))
            .ShouldBe(ToleranceBucket.Unlimited);
    }

    // -- Diff ------------------------------------------------------

    private static TridTolerance.ClassifiedLine L(
        string code, ToleranceBucket bucket, decimal amount)
        => new(code, bucket, amount);

    [Fact]
    public void Diff_identical_snapshots_zero_violations()
    {
        var base_ = new[] { L("origination", ToleranceBucket.Zero, 900m) };
        TridTolerance.Violations(TridTolerance.Diff(base_, base_))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Zero_bucket_any_increase_violates_per_line()
    {
        var base_ = new[] { L("origination", ToleranceBucket.Zero, 900m) };
        var cmp = new[] { L("origination", ToleranceBucket.Zero, 900.01m) };
        var violations = TridTolerance.Violations(
            TridTolerance.Diff(base_, cmp));
        violations.ShouldHaveSingleItem().FeeCode.ShouldBe("origination");
    }

    [Fact]
    public void Ten_percent_exactly_at_cap_does_not_violate()
    {
        var base_ = new[]
        {
            L("appraisal", ToleranceBucket.TenPercent, 600m),
            L("recording-fee", ToleranceBucket.TenPercent, 400m),
        };
        // Aggregate +$100 on $1000 baseline = exactly 10%.
        var cmp = new[]
        {
            L("appraisal", ToleranceBucket.TenPercent, 660m),
            L("recording-fee", ToleranceBucket.TenPercent, 440m),
        };
        TridTolerance.Violations(TridTolerance.Diff(base_, cmp))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Ten_percent_past_cap_violates_on_aggregate()
    {
        var base_ = new[]
        {
            L("appraisal", ToleranceBucket.TenPercent, 600m),
            L("recording-fee", ToleranceBucket.TenPercent, 400m),
        };
        var cmp = new[]
        {
            L("appraisal", ToleranceBucket.TenPercent, 660m),
            L("recording-fee", ToleranceBucket.TenPercent, 440.01m),
        };
        var violations = TridTolerance.Violations(
            TridTolerance.Diff(base_, cmp));
        var v = violations.ShouldHaveSingleItem();
        v.Bucket.ShouldBe(ToleranceBucket.TenPercent);
        v.Rule.ShouldBe("ten-percent-aggregate");
    }

    [Fact]
    public void Ten_percent_mixed_increase_decrease_nets()
    {
        // One fee up $80, another down $50 → net +$30 on $1000 = ok.
        var base_ = new[]
        {
            L("appraisal", ToleranceBucket.TenPercent, 600m),
            L("recording-fee", ToleranceBucket.TenPercent, 400m),
        };
        var cmp = new[]
        {
            L("appraisal", ToleranceBucket.TenPercent, 680m),
            L("recording-fee", ToleranceBucket.TenPercent, 350m),
        };
        TridTolerance.Violations(TridTolerance.Diff(base_, cmp))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Unlimited_never_violates()
    {
        var base_ = new[] { L("escrow", ToleranceBucket.Unlimited, 100m) };
        var cmp = new[] { L("escrow", ToleranceBucket.Unlimited, 99999m) };
        TridTolerance.Violations(TridTolerance.Diff(base_, cmp))
            .ShouldBeEmpty();
    }

    [Fact]
    public void New_fee_in_zero_bucket_violates()
    {
        var base_ = new[] { L("origination", ToleranceBucket.Zero, 900m) };
        var cmp = new[]
        {
            L("origination", ToleranceBucket.Zero, 900m),
            L("surprise", ToleranceBucket.Zero, 50m),
        };
        var violations = TridTolerance.Violations(
            TridTolerance.Diff(base_, cmp));
        violations.ShouldHaveSingleItem().FeeCode.ShouldBe("surprise");
    }

    [Fact]
    public void Removed_fee_is_negative_variance_no_violation()
    {
        var base_ = new[] { L("origination", ToleranceBucket.Zero, 900m) };
        var cmp = Array.Empty<TridTolerance.ClassifiedLine>();
        var diff = TridTolerance.Diff(base_, cmp);
        diff.ShouldHaveSingleItem().Lines.ShouldHaveSingleItem()
            .Delta.ShouldBe(-900m);
        TridTolerance.Violations(diff).ShouldBeEmpty();
    }
}
