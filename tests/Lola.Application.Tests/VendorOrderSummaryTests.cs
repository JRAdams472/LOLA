using System.Text.Json.Nodes;
using Lola.Application.Vendors;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-229 — the normalized summary is the only result-derived
/// string the officer list surface renders. Every kind yields a
/// human line from decision-grade fields; PII-adjacent members
/// (externalId, raw report fields) never appear in the output.
public sealed class VendorOrderSummaryTests
{
    private static JsonObject Report(VendorKind kind) => kind switch
    {
        VendorKind.Credit => new JsonObject
        {
            ["decisionScore"] = 744,
            ["bureaus"] = new JsonArray(
                new JsonObject { ["score"] = 742 },
                new JsonObject { ["score"] = 738 },
                new JsonObject { ["score"] = 751 }),
            ["externalId"] = "stub-credit-x"
        },
        VendorKind.Voe => new JsonObject
        {
            ["employmentStatus"] = "active",
            ["tenureMonths"] = 56
        },
        VendorKind.Voi => new JsonObject
        {
            ["incomeVerified"] = true,
            ["annualizedBaseIncome"] = 118500
        },
        VendorKind.Flood => new JsonObject
        {
            ["floodZone"] = "X",
            ["insuranceRequired"] = false
        },
        VendorKind.Fraud => new JsonObject
        {
            ["ofacMatch"] = false,
            ["identityScore"] = 92
        },
        VendorKind.Aus => new JsonObject
        {
            ["recommendation"] = "approve-eligible",
            ["riskClassification"] = "accept"
        },
        _ => new JsonObject()
    };

    [Theory]
    [InlineData(VendorKind.Credit, "score 744 · 3 bureaus")]
    [InlineData(VendorKind.Voe, "employment active · 56mo tenure")]
    [InlineData(VendorKind.Voi, "$118,500/yr verified")]
    [InlineData(VendorKind.Flood,
        "zone X · no insurance required")]
    [InlineData(VendorKind.Fraud, "OFAC clear · identity 92")]
    [InlineData(VendorKind.Aus, "approve-eligible · accept risk")]
    public void Per_kind_reports_summarize_to_decision_lines(
        VendorKind kind, string expected)
    {
        var summary = VendorOrderSummary.Summarize(
            kind, Report(kind));
        summary.ShouldBe(expected);
        // Nothing beyond the normalized line — no correlation ids.
        summary!.ShouldNotContain("stub-credit-x");
        summary!.ShouldNotContain("externalId");
    }

    [Fact]
    public void Null_report_yields_null_summary()
    {
        VendorOrderSummary.Summarize(VendorKind.Credit, null)
            .ShouldBeNull();
    }

    [Fact]
    public void Missing_fields_degrade_to_dashes_not_exceptions()
    {
        var summary = VendorOrderSummary.Summarize(
            VendorKind.Credit, new JsonObject());
        summary.ShouldBe("score — · 0 bureaus");
    }
}
