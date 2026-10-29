using Lola.Application.Pricing;
using Lola.Domain.Applications;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-129: the 1003 → canonical PII-free scenario mapper — catalog
/// paths only, enum normalization, credit score+model (model-agnostic:
/// FICO or VantageScore), absent-not-zero for missing values.
public sealed class ScenarioMapperTests
{
    private static ApplicationDocument Doc(Dictionary<string, string> fields)
    {
        var d = ApplicationDocument.Empty();
        foreach (var (k, v) in fields) d.Set(k, v);
        return d;
    }

    [Fact]
    public void Maps_full_scenario()
    {
        var s = ScenarioMapper.Map(Doc(new()
        {
            ["loan_purpose"] = "purchase",
            ["loan.product_type"] = "conventional",
            ["loan.base_amount"] = "425000",
            ["loan_term_months"] = "360",
            ["amortization_type"] = "fixed",
            ["loan.ltv"] = "75.9",
            ["loan.cltv"] = "75.9",
            ["subject_property.units"] = "1",
            ["subject_property.property_type"] = "single_family",
            ["subject_property.usage"] = "primary_residence",
            ["subject_property.state"] = "IL",
            ["subject_property.est_value"] = "560000",
            ["party[0].credit_score"] = "740",
            ["party[0].credit_model"] = "vantage",
            ["declaration.first_time_buyer"] = "false",
            ["party[0].employment[0].self_employed"] = "false",
        }), lockTermDays: 30);

        s.Loan.ShouldBe(new LoanSection(
            "purchase", "conventional", 425000m, 360, "fixed",
            null, 75.9m, 75.9m, 1));
        s.Property.ShouldBe(new PropertySection(
            "sfr", "primary", "IL", 560000m, null));
        s.Borrower.ShouldBe(new BorrowerSection(740, "VantageScore", false, false));
        s.Scenario.ShouldBe(new ScenarioSection(30, null));
    }

    [Fact]
    public void Missing_fields_emit_absent_not_zero()
    {
        var s = ScenarioMapper.Map(Doc(new() { ["loan_amount"] = "100000" }));
        s.Loan.Amount.ShouldBe(100000m); // falls back to loan_amount
        s.Loan.Ltv.ShouldBeNull();
        s.Borrower.CreditScore.ShouldBeNull();
        s.Borrower.CreditModel.ShouldBeNull();
        s.Property.State.ShouldBeNull();
    }

    [Fact]
    public void Unmapped_enum_values_emit_absent()
    {
        var s = ScenarioMapper.Map(Doc(new()
        {
            ["loan_purpose"] = "construction", // no MPEx vocabulary match
            ["loan.product_type"] = "reverse_mortgage",
        }));
        s.Loan.Purpose.ShouldBeNull();
        s.Loan.Type.ShouldBeNull();
    }

    [Fact]
    public void Emits_no_identity_fields()
    {
        var s = ScenarioMapper.Map(Doc(new()
        {
            ["party[0].first_name"] = "Ada",
            ["party[0].ssn"] = "123456789",
            ["party[0].dob"] = "1985-12-10",
            ["party[0].email"] = "a@x.dev",
            ["party[0].credit_score"] = "720",
        }));
        // The record shape itself has no identity members — this proves
        // serialization can't smuggle them through extra fields either.
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        json.ShouldNotContain("Ada");
        json.ShouldNotContain("123456789");
        json.ShouldNotContain("a@x.dev");
        json.ShouldContain("720");
    }
}
