using Lola.Domain.Hmda;
using Shouldly;

namespace Lola.Domain.Tests;

public class HmdaTests
{
    // ---- ULI — Appendix C, fixture-proven against the CFPB published
    // example: LEI 10BX939C5543TQA1144M + loan id 999143X → check digit
    // 38 (FIG Table 2 / Appendix C worked example). ----

    [Fact]
    public void Uli_check_digit_matches_cfpb_published_example()
    {
        var uli = Uli.Generate("10BX939C5543TQA1144M", "999143X");
        Assert.Equal("10BX939C5543TQA1144M999143X38", uli);
    }

    [Fact]
    public void Uli_validation_round_trips()
    {
        var uli = Uli.Generate("10BX939C5543TQA1144M", "LOAN000001");
        Assert.True(Uli.Validate(uli));
    }

    [Fact]
    public void Uli_validation_rejects_single_character_mutation()
    {
        var uli = Uli.Generate("10BX939C5543TQA1144M", "999143X");
        var mutated = uli[..^3] + (uli[^3] == 'X' ? 'Y' : 'X') + uli[^2..];
        Assert.False(Uli.Validate(mutated));
    }

    [Fact]
    public void Uli_rejects_non_alphanumeric_suffix()
        => Assert.Throws<ArgumentException>(() => Uli.Generate("10BX939C5543TQA1144M", "BAD-CH"));

    [Fact]
    public void Uli_rejects_wrong_lei_length()
        => Assert.Throws<ArgumentException>(() => Uli.Generate("SHORT", "999143X"));

    [Fact]
    public void Uli_rejects_oversized_suffix()
        => Assert.Throws<ArgumentException>(() => Uli.Generate("10BX939C5543TQA1144M", new string('1', 24)));

    // ---- Assembler derivations ----

    private static LarInput FullInput() => new(
        Lei: "10BX939C5543TQA1144M", UliSuffix: "999143X",
        ApplicationDate: "2026-03-01", LoanType: "fha", LoanPurpose: "cash_out_refinance",
        ConstructionMethod: "site_built", OccupancyType: "principal_residence",
        LoanAmount: 300_000m, ActionTaken: "originated", ActionTakenDate: "2026-04-15",
        StreetAddress: "1 Main St", City: "Springfield", State: "IL", Zip: "62701", CensusTract: "1234.56",
        Ethnicity1: "not_hispanic_or_latino", Ethnicity2: null,
        Race1: "white", Race2: null, Sex: "female",
        CollectionMethod: "not_visual", Dob: new DateOnly(1980, 6, 15), AsOfDate: new DateOnly(2026, 4, 15),
        Income: 96_000m, InterestRate: 6.5m, PrepaymentPenaltyTermMonths: null,
        DtiPercent: 36.5m, CltvPercent: 80m, LoanTermMonths: 360m, IntroRatePeriodMonths: null,
        BalloonPayment: "no", InterestOnlyPayment: "no", NegativeAmortization: "no", OtherNonamortizing: "no",
        PropertyValue: 375_000m, ManufacturedSecuredType: "not_applicable", ManufacturedLandInterest: "not_applicable",
        TotalUnits: 1m, MultifamilyAffordableUnits: null,
        SubmissionOfApplication: "directly", InitiallyPayableTo: "your_institution",
        NmlsId: "12345", ReverseMortgage: "no", OpenEndLineOfCredit: "no", BusinessOrCommercial: "no",
        Apr: 6.75m, Apor: 5.25m, HoepaHighCost: false,
        OriginationCharges: 1500m, DiscountPoints: 0m, LenderCredits: 0m);

    [Fact]
    public void Assembler_maps_catalog_vocabulary_to_fig_codes()
    {
        var r = LarAssembler.Assemble(FullInput());
        Assert.Equal("2", r["loan_type"]);               // FHA
        Assert.Equal("32", r["loan_purpose"]);           // cash-out refi
        Assert.Equal("1", r["construction_method"]);     // site-built
        Assert.Equal("1", r["occupancy_type"]);          // principal residence
        Assert.Equal("1", r["action_taken"]);            // originated
        Assert.Equal("2", r["ethnicity_1"]);             // not hispanic
        Assert.Equal("5", r["race_1"]);                  // white
        Assert.Equal("2", r["sex"]);                     // female
        Assert.Equal("2", r["hoepa_status"]);            // not high-cost
        Assert.Equal("2", r["balloon_payment"]);         // no
    }

    [Fact]
    public void Assembler_formats_dates_yyyyMMdd()
    {
        var r = LarAssembler.Assemble(FullInput());
        Assert.Equal("20260301", r["application_date"]);
        Assert.Equal("20260415", r["action_taken_date"]);
    }

    [Fact]
    public void Assembler_derives_rate_spread_as_apr_minus_apor()
    {
        var r = LarAssembler.Assemble(FullInput());
        Assert.Equal("1.50", r["rate_spread"]);
    }

    [Fact]
    public void Assembler_rate_spread_is_na_without_atr_evaluation()
    {
        var input = FullInput() with { Apr = null, Apor = null, HoepaHighCost = null };
        var r = LarAssembler.Assemble(input);
        Assert.Equal("NA", r["rate_spread"]);
        Assert.Equal("NA", r["hoepa_status"]);
    }

    [Fact]
    public void Assembler_income_rounds_to_thousands()
    {
        var r = LarAssembler.Assemble(FullInput());
        Assert.Equal("96", r["income"]);  // $96,000 → 96 ($000s)
    }

    [Fact]
    public void Assembler_age_computed_against_asof_date()
    {
        var r = LarAssembler.Assemble(FullInput());  // dob 1980-06-15, asof 2026-04-15 → not yet birthday
        Assert.Equal("45", r["age"]);
    }

    [Fact]
    public void Assembler_age_rolls_after_birthday()
    {
        var input = FullInput() with { AsOfDate = new DateOnly(2026, 6, 16) };
        var r = LarAssembler.Assemble(input);
        Assert.Equal("46", r["age"]);
    }

    [Fact]
    public void Assembler_missing_inputs_become_explicit_na_never_blank_invention()
    {
        var input = FullInput() with { CensusTract = null, InterestRate = null, CltvPercent = null, DtiPercent = null };
        var r = LarAssembler.Assemble(input);
        Assert.Equal("NA", r["census_tract"]);
        Assert.Equal("NA", r["rate_of_interest"]);
        Assert.Equal("NA", r["combined_loan_to_value"]);
        Assert.Equal("NA", r["debt_to_income_ratio"]);
    }

    [Fact]
    public void Assembler_emits_every_export_column_exactly_once()
    {
        var r = LarAssembler.Assemble(FullInput());
        foreach (var col in LarSchema.ExportColumns)
            Assert.True(r.ContainsKey(col), $"missing column {col}");
    }

    [Fact]
    public void Assembler_aus_columns_na_until_m25()
    {
        var r = LarAssembler.Assemble(FullInput());
        Assert.Equal("NA", r["aus_1"]);
        Assert.Equal("NA", r["aus_result_1"]);
    }

    // ---- Edit checks: every edit must be able to fire AND pass ----

    private static IReadOnlyDictionary<string, string> CleanRow() => LarAssembler.Assemble(FullInput());

    private static EditResult Fire(string editId, IReadOnlyDictionary<string, string> row) =>
        EditChecks.Run(row).Single(x => x.EditId == editId);

    [Fact]
    public void Clean_row_passes_the_whole_battery()
    {
        var results = EditChecks.Run(CleanRow());
        var failures = results.Where(x => x.Result != "pass").ToList();
        Assert.True(failures.Count == 0,
            "unexpected: " + string.Join("; ", failures.Select(f => $"{f.EditId}:{f.Detail}")));
    }

    [Fact]
    public void Battery_emits_exactly_one_result_per_edit()
    {
        var results = EditChecks.Run(CleanRow());
        Assert.Equal(EditChecks.All.Count, results.Count);
        Assert.Equal(EditChecks.All.Select(e => e.Id), results.Select(x => x.EditId));
    }

    [Fact]
    public void V608_fires_on_corrupted_uli_check_digit()
    {
        var row = new Dictionary<string, string>(CleanRow()) { ["uli"] = "10BX939C5543TQA1144M999143X99" };
        Assert.Equal("fail", Fire("V608", row).Result);
    }

    [Fact]
    public void V610_fires_on_malformed_application_date()
    {
        var row = new Dictionary<string, string>(CleanRow()) { ["application_date"] = "2026-03-01" };
        Assert.Equal("fail", Fire("V610", row).Result);
    }

    [Fact]
    public void V619_fires_on_out_of_range_action_taken()
    {
        var row = new Dictionary<string, string>(CleanRow()) { ["action_taken"] = "9" };
        Assert.Equal("fail", Fire("V619", row).Result);
    }

    [Fact]
    public void V638_fires_on_implausible_age()
    {
        var row = new Dictionary<string, string>(CleanRow()) { ["age"] = "200" };
        Assert.Equal("fail", Fire("V638", row).Result);
        var ok = new Dictionary<string, string>(CleanRow()) { ["age"] = "130" };
        Assert.Equal("pass", Fire("V638", ok).Result);
    }

    [Fact]
    public void V662_fires_on_invalid_hoepa_status()
    {
        var row = new Dictionary<string, string>(CleanRow()) { ["hoepa_status"] = "3" };
        Assert.Equal("fail", Fire("V662", row).Result);
    }

    [Fact]
    public void Quality_edits_warn_never_fail()
    {
        var row = new Dictionary<string, string>(CleanRow()) { ["rate_of_interest"] = "30" };
        var r = Fire("Q603", row);
        Assert.Equal("warn", r.Result);  // quality tier reports as warn, not fail
    }

    [Fact]
    public void Q603_boundary_rate_25_vs_25_01()
    {
        var at = new Dictionary<string, string>(CleanRow()) { ["rate_of_interest"] = "25" };
        Assert.Equal("pass", Fire("Q603", at).Result);
        var over = new Dictionary<string, string>(CleanRow()) { ["rate_of_interest"] = "25.01" };
        Assert.Equal("warn", Fire("Q603", over).Result);
    }

    [Fact]
    public void Q600_fires_when_amount_exceeds_300pct_of_value()
    {
        var row = new Dictionary<string, string>(CleanRow())
        { ["loan_amount"] = "1000000", ["property_value"] = "300000" };
        Assert.Equal("warn", Fire("Q600", row).Result);
        var ok = new Dictionary<string, string>(CleanRow())
        { ["loan_amount"] = "899999", ["property_value"] = "300000" };
        Assert.Equal("pass", Fire("Q600", ok).Result);
    }

    [Fact]
    public void Q608_fires_on_originated_loan_with_zero_spread()
    {
        var row = new Dictionary<string, string>(CleanRow()) { ["rate_spread"] = "0" };
        Assert.Equal("warn", Fire("Q608", row).Result);
    }

    [Fact]
    public void Q608_does_not_fire_without_action_taken_originated()
    {
        var row = new Dictionary<string, string>(CleanRow()) { ["rate_spread"] = "0", ["action_taken"] = "3" };
        Assert.Equal("pass", Fire("Q608", row).Result);
    }

    [Fact]
    public void Q622_fires_when_incomplete_action_lacks_date()
    {
        var row = new Dictionary<string, string>(CleanRow()) { ["action_taken"] = "5", ["action_taken_date"] = "NA" };
        Assert.Equal("warn", Fire("Q622", row).Result);
    }

    [Fact]
    public void Quality_edits_never_fire_on_na_inputs()
    {
        var row = new Dictionary<string, string>(CleanRow())
        { ["loan_amount"] = "NA", ["property_value"] = "NA", ["rate_spread"] = "NA" };
        Assert.Equal("pass", Fire("Q600", row).Result);
        Assert.Equal("pass", Fire("Q608", row).Result);
    }
}

/// M24 P1 — catalog regression proof: the new HMDA paths resolve in
/// the field catalog (PATCH validation acceptance, same discipline as
/// M23 — `SpecFor` null means the doc PATCH endpoints would reject it).
public class HmdaCatalogTests
{
    [Theory]
    [InlineData("hmda.action_taken")]
    [InlineData("hmda.action_taken_date")]
    [InlineData("hmda.submission_of_application")]
    [InlineData("hmda.initially_payable_to")]
    [InlineData("hmda.income")]
    [InlineData("loan.reverse_mortgage")]
    [InlineData("loan.open_end_line_of_credit")]
    [InlineData("loan.business_or_commercial_purpose")]
    [InlineData("loan.interest_only_payment")]
    [InlineData("loan.balloon_payment")]
    [InlineData("loan.negative_amortization")]
    [InlineData("loan.other_nonamortizing_features")]
    [InlineData("loan.prepayment_penalty_term_months")]
    [InlineData("loan.intro_rate_period_months")]
    [InlineData("loan.origination_charges")]
    [InlineData("loan.discount_points")]
    [InlineData("loan.lender_credits")]
    [InlineData("subject_property.census_tract")]
    [InlineData("subject_property.construction_method")]
    [InlineData("subject_property.manufactured_home_secured_property_type")]
    [InlineData("subject_property.manufactured_home_land_property_interest")]
    [InlineData("subject_property.multifamily_affordable_units")]
    [InlineData("demographics.ethnicity_1")]
    [InlineData("demographics.ethnicity_2")]
    [InlineData("demographics.race_1")]
    [InlineData("demographics.race_2")]
    [InlineData("demographics.sex_detail")]
    public void Hmda_paths_resolve_in_catalog(string path)
        => Lola.Domain.Applications.FieldCatalog.Current.SpecFor(path)
            .ShouldNotBeNull($"path '{path}' must resolve — PATCH would reject it otherwise");

    [Fact]
    public void Enum_fields_carry_their_valid_values()
    {
        var spec = Lola.Domain.Applications.FieldCatalog.Current.SpecFor("hmda.action_taken");
        spec.ShouldNotBeNull();
        spec.Enum.ShouldNotBeNull();
        spec.Enum.ShouldContain("originated");
        spec.Enum.ShouldContain("denied");
    }
}
