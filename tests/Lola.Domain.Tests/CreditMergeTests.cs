using Lola.Domain.Credit;
using Xunit;

namespace Lola.Domain.Tests;

/// M26 P1 — tri-merge merge rules + liability reconciliation, pinned
/// against hand-built bureau files (format-independent; wire parsers
/// are proven separately against fixtures).
public class CreditMergeTests
{
    private static BureauTradeline Tl(string creditor, string? mask,
        string type = "revolving", decimal? balance = 1000m,
        decimal? payment = 50m, string? status = "current",
        string? reported = "2026-10-01",
        IReadOnlyList<LateEvent>? lates = null)
        => new(creditor, mask, type, null, balance, payment, status,
            reported, lates ?? []);

    private static BureauFile Bureau(string name, int? score,
        string? model, params BureauTradeline[] tls)
        => new(name, score, model, tls);

    // —— dedupe ————————————————————————————————————————————————————

    [Fact]
    public void Same_mask_across_bureaus_merges_into_one_tradeline()
    {
        var eq = Bureau("equifax", 720, "fico-10t",
            Tl("CAPITAL ONE", "****1234"));
        var tu = Bureau("transunion", 710, "vantagescore-4.0",
            Tl("Capital One NA", "****1234"));
        var merged = TriMergeMerger.Merge([eq, tu]);
        Assert.Single(merged.Tradelines);
        Assert.Equal(["equifax", "transunion"],
            merged.Tradelines[0].Bureaus);
    }

    [Fact]
    public void Distinct_masks_never_merge_even_same_creditor()
    {
        var eq = Bureau("equifax", 720, "fico-10t",
            Tl("CAPITAL ONE", "****1234"),
            Tl("CAPITAL ONE", "****5678"));
        var merged = TriMergeMerger.Merge([eq]);
        Assert.Equal(2, merged.Tradelines.Count);
    }

    [Fact]
    public void Unique_single_bureau_tradeline_survives()
    {
        var eq = Bureau("equifax", 720, "fico-10t",
            Tl("CAPITAL ONE", "****1234"));
        var tu = Bureau("transunion", 710, "vantagescore-4.0",
            Tl("LOCAL CREDIT UNION", "****9999"));
        var merged = TriMergeMerger.Merge([eq, tu]);
        Assert.Equal(2, merged.Tradelines.Count);
        Assert.Equal(["transunion"],
            merged.Tradelines[1].Bureaus);
    }

    // —— conflict resolution ————————————————————————————————————————

    [Fact]
    public void Most_recent_reporting_wins_balance_and_payment()
    {
        var eq = Bureau("equifax", null, null,
            Tl("CAPITAL ONE", "****1234", balance: 1000m, payment: 50m,
                reported: "2026-08-01"));
        var tu = Bureau("transunion", null, null,
            Tl("CAPITAL ONE", "****1234", balance: 800m, payment: 45m,
                reported: "2026-10-01"));
        var merged = TriMergeMerger.Merge([eq, tu]);
        Assert.Equal(800m, merged.Tradelines[0].Balance);
        Assert.Equal(45m, merged.Tradelines[0].MonthlyPayment);
    }

    [Fact]
    public void Worst_status_wins_for_delinquency()
    {
        var eq = Bureau("equifax", null, null,
            Tl("CAPITAL ONE", "****1234", status: "current"));
        var tu = Bureau("transunion", null, null,
            Tl("CAPITAL ONE", "****1234", status: "60"));
        var merged = TriMergeMerger.Merge([eq, tu]);
        Assert.Equal("60", merged.Tradelines[0].Status);
    }

    // —— representative score ————————————————————————————————————————

    [Fact]
    public void Representative_is_mid_of_three_with_model_of_chosen()
    {
        var files = new[]
        {
            Bureau("equifax", 720, "fico-10t"),
            Bureau("transunion", 700, "vantagescore-4.0"),
            Bureau("experian", 740, "fico-10t"),
        };
        var merged = TriMergeMerger.Merge(files);
        Assert.Equal(720, merged.RepresentativeScore);
        Assert.Equal("fico-10t", merged.RepresentativeModel);
    }

    [Fact]
    public void Representative_is_lower_of_two()
    {
        var files = new[]
        {
            Bureau("equifax", 720, "fico-10t"),
            Bureau("transunion", 700, "vantagescore-4.0"),
        };
        var merged = TriMergeMerger.Merge(files);
        Assert.Equal(700, merged.RepresentativeScore);
        Assert.Equal("vantagescore-4.0", merged.RepresentativeModel);
    }

    [Fact]
    public void Score_models_never_blend_across_bureaus()
    {
        var files = new[]
        {
            Bureau("equifax", 720, "fico-10t"),
            Bureau("transunion", 700, "vantagescore-4.0"),
        };
        var merged = TriMergeMerger.Merge(files);
        Assert.Equal(2, merged.Scores.Count);
        Assert.Equal("fico-10t", merged.Scores[0].Model);
        Assert.Equal("vantagescore-4.0", merged.Scores[1].Model);
    }

    // —— late extraction ————————————————————————————————————————————

    [Fact]
    public void Late_events_union_by_month_worst_severity_wins()
    {
        var eq = Bureau("equifax", null, null,
            Tl("CAPITAL ONE", "****1234", lates:
                [new LateEvent("2026-06", 30)]));
        var tu = Bureau("transunion", null, null,
            Tl("CAPITAL ONE", "****1234", lates:
                [new LateEvent("2026-06", 60),
                 new LateEvent("2026-07", 30)]));
        var merged = TriMergeMerger.Merge([eq, tu]);
        var lates = merged.Tradelines[0].Lates;
        Assert.Equal(2, lates.Count);
        Assert.Equal(60, lates.First(l => l.Month == "2026-06").Severity);
    }

    [Fact]
    public void Lates_outside_24_month_lookback_do_not_count()
    {
        var cutoff = TriMergeMerger.LookbackCutoff();
        var inside = DateTime.UtcNow.AddMonths(-3).ToString("yyyy-MM");
        var outside = DateTime.UtcNow.AddMonths(-30).ToString("yyyy-MM");
        var tl = new MergedTradeline("X", null, "revolving", null, null,
            null, null, ["equifax"],
            [new LateEvent(inside, 30), new LateEvent(outside, 90)]);
        var (l30, l60, l90) = TriMergeMerger.CountLates([tl], cutoff);
        Assert.Equal(1, l30);
        Assert.Equal(0, l60);
        Assert.Equal(0, l90);
    }

    [Fact]
    public void Severities_count_in_their_own_buckets()
    {
        var recent = DateTime.UtcNow.AddMonths(-2).ToString("yyyy-MM");
        var tl = new MergedTradeline("X", null, "revolving", null, null,
            null, null, ["equifax"],
            [new LateEvent(recent, 30), new LateEvent(recent, 60),
             new LateEvent(recent, 90)]);
        var (l30, l60, l90) = TriMergeMerger.CountLates([tl],
            TriMergeMerger.LookbackCutoff());
        Assert.Equal((1, 1, 1), (l30, l60, l90));
    }

    // —— liability reconciliation ————————————————————————————————————

    private static LiabilityReconciler.DocLiability Doc(int i,
        string? creditor = null, string? account = null,
        decimal? payment = null, decimal? balance = null,
        string? type = null)
        => new(i, type, creditor, account, payment, balance);

    [Fact]
    public void Exact_account_match_updates_balance_and_payment()
    {
        var existing = new[]
        {
            Doc(0, creditor: "Capital One", account: "****1234",
                payment: 50m, balance: 1000m),
        };
        var tl = new MergedTradeline("CAPITAL ONE", "****1234",
            "revolving", 800m, 45m, "current", "2026-10-01",
            ["equifax"], []);
        var writes = LiabilityReconciler.Reconcile(existing, [tl]);
        Assert.Contains(writes,
            w => w.Path == "liability[0].balance" && w.Value == "800");
        Assert.Contains(writes,
            w => w.Path == "liability[0].monthly_payment" && w.Value == "45");
        Assert.DoesNotContain(writes, w => w.Path.Contains("[1]"));
    }

    [Fact]
    public void Creditor_plus_payment_tolerance_matches_without_account()
    {
        var existing = new[]
        {
            Doc(0, creditor: "Capital One NA", account: null,
                payment: 100m, balance: 2000m),
        };
        var tl = new MergedTradeline("CAPITAL ONE", null, "revolving",
            1800m, 105m, "current", "2026-10-01", ["equifax"], []);
        var writes = LiabilityReconciler.Reconcile(existing, [tl]);
        Assert.Contains(writes,
            w => w.Path == "liability[0].balance" && w.Value == "1800");
    }

    [Fact]
    public void Payment_outside_tolerance_does_not_match()
    {
        var existing = new[]
        {
            Doc(0, creditor: "Capital One", payment: 100m),
        };
        var tl = new MergedTradeline("CAPITAL ONE", null, "revolving",
            1800m, 150m, "current", "2026-10-01", ["equifax"], []);
        var writes = LiabilityReconciler.Reconcile(existing, [tl]);
        Assert.DoesNotContain(writes,
            w => w.Path == "liability[0].balance");
        Assert.Contains(writes, w => w.Path.StartsWith("liability[1]."));
    }

    [Fact]
    public void Unmatched_tradeline_appends_with_tri_merge_source()
    {
        var existing = new[]
        {
            Doc(0, creditor: "WELLS FARGO", payment: 200m),
        };
        var tl = new MergedTradeline("DISCOVER", "****7777", "revolving",
            3000m, 75m, "current", "2026-10-01", ["equifax"], []);
        var writes = LiabilityReconciler.Reconcile(existing, [tl]);
        Assert.Contains(writes,
            w => w.Path == "liability[1].source" && w.Value == "tri_merge");
        Assert.Contains(writes,
            w => w.Path == "liability[1].creditor" && w.Value == "DISCOVER");
        Assert.Contains(writes,
            w => w.Path == "liability[1].liability_type"
                && w.Value == "revolving");
    }

    [Fact]
    public void Borrower_entries_are_never_deleted()
    {
        var existing = new[]
        {
            Doc(0, creditor: "SMALL BANK", payment: 80m),
        };
        // No tradelines at all — the borrower entry is untouched.
        var writes = LiabilityReconciler.Reconcile(existing, []);
        Assert.Empty(writes);
    }

    [Fact]
    public void Digit_tail_matching_tolerates_mask_styles()
    {
        var existing = new[]
        {
            Doc(0, creditor: "Capital One", account: "xxxx-1234",
                payment: 50m),
        };
        var tl = new MergedTradeline("CAPITAL ONE", "****1234",
            "revolving", 800m, 45m, "current", "2026-10-01",
            ["equifax"], []);
        var writes = LiabilityReconciler.Reconcile(existing, [tl]);
        Assert.Contains(writes,
            w => w.Path == "liability[0].balance");
    }

    [Fact]
    public void Appended_index_never_collides_with_existing()
    {
        var existing = new[]
        {
            Doc(0, creditor: "A"), Doc(1, creditor: "B"),
        };
        var tl = new MergedTradeline("NEWCREDITOR", null, "auto",
            9000m, 300m, "current", "2026-10-01", ["equifax"], []);
        var writes = LiabilityReconciler.Reconcile(existing, [tl]);
        Assert.Contains(writes, w => w.Path.StartsWith("liability[2]."));
        Assert.DoesNotContain(writes, w => w.Path.StartsWith("liability[0]."));
        Assert.DoesNotContain(writes, w => w.Path.StartsWith("liability[1]."));
    }

    [Fact]
    public void Late_writes_emit_all_three_buckets()
    {
        var writes = LiabilityReconciler.LateWrites(2, 1, 0);
        Assert.Equal(3, writes.Count);
        Assert.Contains(writes,
            w => w.Path == "credit.lates_30" && w.Value == "2");
        Assert.Contains(writes,
            w => w.Path == "credit.lates_90" && w.Value == "0");
    }

    [Fact]
    public void Student_loans_map_to_installment()
    {
        var writes = LiabilityReconciler.Reconcile([],
            [new MergedTradeline("NELNET", null, "student", 20000m, 150m,
                "current", "2026-10-01", ["equifax"], [])]);
        Assert.Contains(writes,
            w => w.Path == "liability[0].liability_type"
                && w.Value == "installment");
    }
}
