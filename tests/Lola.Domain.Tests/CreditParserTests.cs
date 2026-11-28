using Lola.Domain.Credit;
using Xunit;

namespace Lola.Domain.Tests;

/// M26 P1 — parser proof against the synthetic fixtures:
/// MISMO v2.3.1 credit responses per bureau + HouseCanary-shaped AVM
/// JSON. Fixture semantics are pinned here so parser regressions fail
/// loudly.
public class CreditParserTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(FixtureRoot(), name));

    private static string FixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "Lola.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "fixtures", "credit");
    }

    [Fact]
    public void Equifax_fixture_parses_score_model_and_tradelines()
    {
        var outcome = CreditResponseParser.Parse(
            Fixture("equifax-sample.xml"), null);
        Assert.True(outcome.Ok, outcome.Error);
        var file = outcome.File!;
        Assert.Equal("equifax", file.Bureau);
        Assert.Equal(724, file.Score);
        Assert.Equal("FICO 10T", file.ScoreModel);
        Assert.Equal(5, file.Tradelines.Count);
    }

    [Fact]
    public void Transunion_fixture_carries_vantagescore_model_tag()
    {
        var outcome = CreditResponseParser.Parse(
            Fixture("transunion-sample.xml"), null);
        Assert.True(outcome.Ok, outcome.Error);
        Assert.Equal("transunion", outcome.File!.Bureau);
        Assert.Equal(731, outcome.File.Score);
        Assert.Equal("VantageScore 4.0", outcome.File.ScoreModel);
    }

    [Fact]
    public void Tradeline_fields_parse_from_mismo_attributes()
    {
        var outcome = CreditResponseParser.Parse(
            Fixture("equifax-sample.xml"), null);
        var mortgage = outcome.File!.Tradelines.First(
            t => t.Creditor == "FIRST SYNTHETIC MORTGAGE");
        Assert.Equal("****0456", mortgage.AccountMask);
        Assert.Equal("mortgage", mortgage.Type);
        Assert.Equal(286400m, mortgage.Balance);
        Assert.Equal(1875m, mortgage.MonthlyPayment);
        Assert.Equal("2026-10", mortgage.LastReportedDate);
    }

    [Fact]
    public void Payment_grid_decodes_to_month_tagged_late_events()
    {
        var outcome = CreditResponseParser.Parse(
            Fixture("equifax-sample.xml"), null);
        var mortgage = outcome.File!.Tradelines.First(
            t => t.Creditor == "FIRST SYNTHETIC MORTGAGE");
        // Fixture adverse ratings: 2025-02=90, 2025-01=60,
        // 2024-12=30, 2024-04=30 — the grid decode must reproduce them.
        var lates = mortgage.Lates;
        Assert.Contains(lates,
            l => l.Month == "2025-02" && l.Severity == 90);
        Assert.Contains(lates,
            l => l.Month == "2025-01" && l.Severity == 60);
        Assert.Contains(lates,
            l => l.Month == "2024-12" && l.Severity == 30);
        Assert.Contains(lates,
            l => l.Month == "2024-04" && l.Severity == 30);
    }

    [Fact]
    public void Malformed_fixture_yields_typed_failure_never_throws()
    {
        var outcome = CreditResponseParser.Parse(
            Fixture("malformed-truncated-credit.xml"), "equifax");
        Assert.False(outcome.Ok);
        Assert.NotNull(outcome.Error);
        Assert.Null(outcome.File);
    }

    [Fact]
    public void Garbage_input_yields_typed_failure()
    {
        var outcome = CreditResponseParser.Parse("not xml at all", null);
        Assert.False(outcome.Ok);
        Assert.Null(outcome.File);
    }

    [Fact]
    public void Xml_with_dtd_is_rejected()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x \"y\">]>"
            + "<RESPONSE_GROUP><RESPONSE><RESPONSE_DATA>"
            + "<CREDIT_RESPONSE/></RESPONSE_DATA></RESPONSE>"
            + "</RESPONSE_GROUP>";
        var outcome = CreditResponseParser.Parse(xml, null);
        Assert.False(outcome.Ok);
    }

    [Fact]
    public void Empty_response_yields_failure_not_empty_file()
    {
        var outcome = CreditResponseParser.Parse(
            "<root/>", null);
        Assert.False(outcome.Ok);
    }

    // —— AVM —————————————————————————————————————————————————————————

    [Fact]
    public void Avm_fixture_parses_value_confidence_fsd_and_model()
    {
        var json = File.ReadAllText(
            Path.Combine(FixtureRoot(), "avm-response-sample.json"));
        var outcome = AvmResponseParser.Parse(json, "housecanary");
        Assert.True(outcome.Ok, outcome.Error);
        var r = outcome.Result!;
        Assert.Equal(512000m, r.Value);
        Assert.Equal(94m, r.Confidence); // 0.94 normalized to 0-100
        Assert.Equal(0.06m, r.Fsd);
        Assert.Equal(481280m, r.ValueLow);
        Assert.Equal(542720m, r.ValueHigh);
        Assert.Equal("2026-11-14", r.ValuationDate);
        Assert.Contains("SyntheticCanary", r.Model);
        Assert.Equal("housecanary", r.Vendor);
    }

    [Fact]
    public void Avm_malformed_json_yields_typed_failure()
    {
        var outcome = AvmResponseParser.Parse("{not json", "v");
        Assert.False(outcome.Ok);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public void Avm_missing_valuation_yields_typed_failure()
    {
        var outcome = AvmResponseParser.Parse(
            "[{\"property/value\": {\"result\": {}}}]", "v");
        Assert.False(outcome.Ok);
    }

    [Theory]
    [InlineData(0.5, 50)]
    [InlineData(75, 75)]
    [InlineData(1.0, 100)]
    public void Confidence_normalizes_across_scales(double raw,
        double expected)
    {
        Assert.Equal((decimal)expected,
            AvmModel.NormalizeConfidence((decimal)raw));
    }

    [Fact]
    public void Confidence_out_of_range_yields_null_not_invented()
    {
        Assert.Null(AvmModel.NormalizeConfidence(150m));
        Assert.Null(AvmModel.NormalizeConfidence(null));
    }

    // —— full tri-merge fixture round-trip ——————————————————————————

    [Fact]
    public void Three_bureau_fixtures_merge_with_conflicts_resolved()
    {
        var files = new[] { "equifax", "transunion", "experian" }
            .Select(b => CreditResponseParser
                .Parse(Fixture($"{b}-sample.xml"), null).File!)
            .ToList();
        var merged = TriMergeMerger.Merge(files);
        Assert.NotNull(merged.RepresentativeScore);
        Assert.True(merged.Tradelines.Count > 0);
        Assert.All(merged.Tradelines,
            t => Assert.NotEmpty(t.Bureaus));

        // Fixture-pinned conflict resolution (harvest conflict matrix):
        // Capital One ****7788 reported by all three bureaus with
        // balances 4125(EQ 2026-10) / 3890(TU 2026-11) / 4302(XP 2026-09)
        // — most-recent-reporting wins → TU's 3890.
        var capOne = merged.Tradelines.First(
            t => t.AccountMask == "****7788");
        Assert.Equal(3890m, capOne.Balance);
        Assert.Equal(3, capOne.Bureaus.Count);

        // Mortgage ****0456 same shape — TU 2026-11 wins → 285975.
        var mtg = merged.Tradelines.First(
            t => t.AccountMask == "****0456");
        Assert.Equal(285975m, mtg.Balance);

        // Bureau-unique tradelines survive with single-bureau lineage.
        Assert.Single(merged.Tradelines,
            t => t.AccountMask == "****3300");
        Assert.Single(merged.Tradelines,
            t => t.AccountMask == "****4402");
        Assert.Single(merged.Tradelines,
            t => t.AccountMask == "****6505");

        // Score model tags never blend: two FICO 10T + one
        // VantageScore 4.0 — representative is mid-of-three with the
        // CHOSEN file's model.
        Assert.Equal(3, merged.Scores.Count);
        Assert.Contains(merged.Scores,
            s => s.Model == "VantageScore 4.0");
    }
}
