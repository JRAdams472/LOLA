using Lola.Application.Fees;
using Lola.Domain.Applications;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-182: the fee mapper emits the minimal scenario the fee rules
/// need — PII-free by construction (identity paths never consulted).
public sealed class FeeScenarioMapperTests
{
    private static ApplicationDocument Doc()
    {
        var d = ApplicationDocument.Empty();
        d.Set("loan_purpose", "purchase");
        d.Set("loan.product_type", "conventional");
        d.Set("loan.base_amount", "350000");
        d.Set("loan_term_months", "360");
        d.Set("subject_property.state", "IL");
        d.Set("subject_property.zip", "62701");
        d.Set("subject_property.county", "Sangamon");
        d.Set("subject_property.est_value", "560000");
        d.Set("subject_property.purchase_price", "475000");
        d.Set("declaration.va_eligible", "true");
        d.Set("declaration.discount_points", "false");
        d.Set("party[0].employment[0].self_employed", "false");
        // PII that must NOT cross — the mapper never reads these.
        d.Set("party[0].first_name", "Ada");
        d.Set("party[0].ssn", "123456789");
        return d;
    }

    [Fact]
    public void Maps_scenario_fields_from_the_catalog()
    {
        var s = FeeScenarioMapper.Map(Doc());
        s.Loan.Purpose.ShouldBe("purchase");
        s.Loan.Type.ShouldBe("conventional");
        s.Loan.Amount.ShouldBe(350000m);
        s.Loan.TermMonths.ShouldBe(360);
        s.Property.State.ShouldBe("IL");
        // Zip never crosses — the engine's inbound PII guard denies it.
        s.Property.Zip.ShouldBeNull();
        s.Property.County.ShouldBe("Sangamon");
        s.Property.EstValue.ShouldBe(560000m);
        s.Property.PurchasePrice.ShouldBe(475000m);
        s.Borrower.VaEligible.ShouldBe(true);
        s.Borrower.DiscountPoints.ShouldBe(false);
        s.Borrower.SelfEmployed.ShouldBe(false);
    }

    [Fact]
    public void Serialized_scenario_carries_no_identity_fields()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            FeeScenarioMapper.Map(Doc()));
        foreach (var leak in new[] { "Ada", "123456789", "firstName",
            "ssn", "dob", "email", "phone", "address" })
            json.ShouldNotContain(leak);
        // Scenario attributes DO cross — but the vendor-denied zip
        // value must not, or the engine 422s the eval as PII.
        json.ShouldNotContain("62701");
        json.ShouldContain("vaEligible");
    }

    [Fact]
    public void Missing_fields_map_to_null_not_exceptions()
    {
        var s = FeeScenarioMapper.Map(ApplicationDocument.Empty());
        s.Loan.Amount.ShouldBeNull();
        s.Property.Zip.ShouldBeNull();
        s.Borrower.VaEligible.ShouldBeNull();
    }
}
