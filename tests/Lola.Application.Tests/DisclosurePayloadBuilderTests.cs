using Lola.Application.Documents;
using Lola.Application.Errors;
using Lola.Application.Fees;
using Lola.Application.Pricing;
using Lola.Domain.Applications;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-189 payload-boundary proofs (invariant 2, documents variant):
/// the builder emits EXACTLY each template's manifest field set — no
/// more, no less — and PII-class source fields that no template
/// declared (ssn, dob, ssn-adjacent) never cross. Adverse-action
/// reason codes are allowlisted so no free text can smuggle PII.
public sealed class DisclosurePayloadBuilderTests
{
    private const string AppId = "app-1";

    private static ApplicationDocument Doc()
    {
        var d = ApplicationDocument.Empty();
        d.Set("party[0].first_name", "Ada");
        d.Set("party[0].middle_name", "M");
        d.Set("party[0].last_name", "Lovelace");
        d.Set("party[0].ssn", "123456789");          // must never emit
        d.Set("party[0].dob", "1990-01-01");         // must never emit
        d.Set("party[0].email", "ada@example.com");
        d.Set("party[0].phone", "555-0100");
        d.Set("party[0].address[0].street", "1 Analytical Way");
        d.Set("party[0].address[0].city", "Springfield");
        d.Set("party[0].address[0].state", "IL");
        d.Set("party[0].address[0].zip", "62701");
        d.Set("party[0].employment[0].employer_name", "Engine Co");
        d.Set("party[0].employment[0].self_employed", "false");
        d.Set("loan.base_amount", "350000");
        d.Set("loan_amount", "350000");
        d.Set("loan_purpose", "purchase");
        d.Set("loan_term_months", "360");
        d.Set("loan.product_type", "conventional");
        d.Set("subject_property.street", "42 Babbage Rd");
        d.Set("subject_property.city", "Springfield");
        d.Set("subject_property.state", "IL");
        d.Set("subject_property.zip", "62701");
        d.Set("subject_property.est_value", "560000");
        d.Set("subject_property.purchase_price", "475000");
        d.Set("declaration.intent_to_occupy", "primary");
        return d;
    }

    private static RateLockRecord Lock() => new(
        "lock-1", AppId, "eval-1", "CONV30", 6.5m, 99.5m, 30,
        "Investor Co", "2026-01-01T00:00:00.000Z",
        "2026-02-01T00:00:00.000Z", "actor-1", "active",
        [], null, null, null);

    private static FeeBreakdownDto Fees() => new(
        "feval-1", null, AppId, "stub", "done", null,
        [
            new FeeSectionDto("A",
                [new FeeLineDto("i1", "origination", "Origination",
                    900m, 900m, null, null, 900m)],
                900m, 900m),
            new FeeSectionDto("B",
                [new FeeLineDto("i2", "credit-report", "Credit Report",
                    45m, 45m, null, null, 45m)],
                45m, 45m),
        ],
        945m, 945m, [],
        "2026-01-01T00:00:00.000Z", "2026-01-01T00:00:01.000Z");

    // ---- the minimization contract: emitted == manifest, both ways ----

    [Theory]
    [InlineData("le-package")]
    [InlineData("adverse-action")]
    public void Every_template_emits_exactly_its_manifest_field_set(
        string kind)
    {
        var docs = DisclosurePayloadBuilder.Build(
            kind, AppId, Doc(), Lock(), Fees(),
            kind == "adverse-action" ? ["credit-history"] : null);

        docs.Select(d => d.TemplateId).ShouldBe(
            TemplateManifest.TemplatesForKind(kind));
        foreach (var d in docs)
        {
            var emitted = d.Payload.Select(kv => kv.Key).ToHashSet();
            var declared = TemplateManifest.FieldsFor(d.TemplateId);
            emitted.ShouldBeSubsetOf(declared);   // ⊆ — nothing extra
            declared.ShouldBeSubsetOf(emitted);   // ⊇ — nothing missing
        }
    }

    [Fact]
    public void Payloads_never_emit_undeclared_pii_even_when_present()
    {
        var docs = DisclosurePayloadBuilder.Build(
            "le-package", AppId, Doc(), Lock(), Fees());
        var json = string.Concat(
            docs.Select(d => d.Payload.ToJsonString()));

        json.ShouldNotContain("123456789");
        json.ShouldNotContain("1990-01-01");
        json.ShouldNotContain("ssn");
        json.ShouldNotContain("dob");
    }

    // ---- le-package gating -------------------------------------------

    [Fact]
    public void Le_package_requires_an_active_rate_lock()
    {
        var ex = Should.Throw<DomainException>(() =>
            DisclosurePayloadBuilder.Build(
                "le-package", AppId, Doc(), null, Fees()));
        ex.Status.ShouldBe(409);
    }

    [Fact]
    public void Le_package_requires_a_completed_fee_eval()
    {
        var pending = Fees() with { Status = "pending" };
        var ex = Should.Throw<DomainException>(() =>
            DisclosurePayloadBuilder.Build(
                "le-package", AppId, Doc(), Lock(), pending));
        ex.Status.ShouldBe(409);
    }

    // ---- adverse-action validation -----------------------------------

    [Fact]
    public void Adverse_action_rejects_missing_reason_codes()
    {
        var ex = Should.Throw<DomainException>(() =>
            DisclosurePayloadBuilder.Build(
                "adverse-action", AppId, Doc(), null, null));
        ex.Status.ShouldBe(400);
    }

    [Fact]
    public void Adverse_action_rejects_empty_reason_codes()
    {
        var ex = Should.Throw<DomainException>(() =>
            DisclosurePayloadBuilder.Build(
                "adverse-action", AppId, Doc(), null, null, []));
        ex.Status.ShouldBe(400);
    }

    [Fact]
    public void Adverse_action_rejects_unknown_reason_codes()
    {
        var ex = Should.Throw<DomainException>(() =>
            DisclosurePayloadBuilder.Build("adverse-action", AppId,
                Doc(), null, null, ["credit-history", "made-up-reason"]));
        ex.Status.ShouldBe(400);
        ex.Message.ShouldContain("made-up-reason");
    }

    [Fact]
    public void Adverse_action_maps_codes_to_reason_texts()
    {
        var docs = DisclosurePayloadBuilder.Build(
            "adverse-action", AppId, Doc(), null, null,
            ["credit-history", "insufficient-income"]);
        var reasons = docs[0].Payload["reasons"]!
            .AsArray().Select(n => n!.GetValue<string>()).ToList();
        reasons.ShouldBe([
            "Credit history",
            "Income insufficient for amount requested"]);
    }

    // ---- content correctness ------------------------------------------

    [Fact]
    public void Loan_estimate_computes_principal_and_interest()
    {
        var docs = DisclosurePayloadBuilder.Build(
            "le-package", AppId, Doc(), Lock(), Fees());
        var le = docs.Single(d => d.TemplateId == "loan-estimate");
        // 350k @ 6.5% / 360mo ≈ 2212.24 — standard amortization.
        le.Payload["monthlyPrincipalInterest"]!
            .GetValue<decimal>().ShouldBe(2212.24m);
        le.Payload["interestRate"]!.GetValue<decimal>().ShouldBe(6.5m);
        le.Payload["productCode"]!.GetValue<string>().ShouldBe("CONV30");
        le.Payload["borrowerNames"]!.AsArray()[0]!
            .GetValue<string>().ShouldBe("Ada M Lovelace");
    }

    [Fact]
    public void Loan_estimate_estimates_cash_to_close()
    {
        var docs = DisclosurePayloadBuilder.Build(
            "le-package", AppId, Doc(), Lock(), Fees());
        var le = docs.Single(d => d.TemplateId == "loan-estimate");
        // 475000 price − 350000 loan + 945 fees = 125945.
        le.Payload["estimatedCashToClose"]!
            .GetValue<decimal>().ShouldBe(125945m);
    }

    [Fact]
    public void Unknown_kind_is_a_400()
    {
        var ex = Should.Throw<DomainException>(() =>
            DisclosurePayloadBuilder.Build(
                "gnaf-package", AppId, Doc(), Lock(), Fees()));
        ex.Status.ShouldBe(400);
    }
}
