using System.Text.Json;
using Lola.Application.Vendors;
using Lola.Infrastructure.Vendors;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// MPE-226 stub contract: deterministic receipts keyed on the order
/// payload, normalized per-kind report shapes, and — invariant 3 —
/// no request PII echoed into any receipt or report.
public sealed class StubVendorAdapterTests
{
    private readonly StubVendorAdapter _adapter = new();

    private static CreditOrderRequest Credit() => new(
        "Marina", "Sample", "000-12-3456", "1990-04-01",
        "1 Main St", "Austin", "TX", "78701");

    [Fact]
    public void Covers_all_five_kinds()
        => _adapter.Kinds.ShouldBe([
            VendorKind.Credit, VendorKind.Voe, VendorKind.Voi,
            VendorKind.Flood, VendorKind.Fraud, VendorKind.Aus]);

    [Fact]
    public async Task Submit_returns_a_deterministic_kind_tagged_receipt()
    {
        var first = await _adapter.SubmitAsync(Credit(), null);
        var replay = await _adapter.SubmitAsync(Credit(), null);

        first.ExternalId.ShouldStartWith("stub-credit-");
        first.ShouldBe(replay); // replay → same receipt (vendor idempotency)
        first.ReceivedUtc.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Submit_ids_differ_per_kind_and_payload()
    {
        var credit = await _adapter.SubmitAsync(Credit(), null);
        var fraud = await _adapter.SubmitAsync(new FraudOrderRequest(
            "Marina", "Sample", "000-12-3456", "1990-04-01"), null);

        credit.ExternalId.ShouldStartWith("stub-credit-");
        fraud.ExternalId.ShouldStartWith("stub-fraud-");
        credit.ExternalId.ShouldNotBe(fraud.ExternalId);
    }

    [Fact]
    public async Task Poll_returns_the_normalized_report_per_kind()
    {
        foreach (var kind in _adapter.Kinds)
        {
            var receipt = new VendorReceipt(
                $"stub-{kind.ToString().ToLowerInvariant()}-abc",
                "2026-11-17T00:00:00Z");
            var result = await _adapter.PollAsync(receipt, null);

            result.ShouldNotBeNull();
            result!.Kind.ShouldBe(kind);
            result.Outcome.ShouldBe("completed");
            result.Report["externalId"]!.GetValue<string>()
                .ShouldBe(receipt.ExternalId);
        }
    }

    [Theory]
    [InlineData(VendorKind.Credit, "reportType,bureaus,decisionScore,tradelineCount")]
    [InlineData(VendorKind.Voe, "verificationType,employerVerified,employmentStatus,tenureMonths")]
    [InlineData(VendorKind.Voi, "verificationType,incomeVerified,annualizedBaseIncome")]
    [InlineData(VendorKind.Flood, "certificationType,floodZone,sfha,insuranceRequired")]
    [InlineData(VendorKind.Fraud, "screeningType,ofacMatch,identityScore,ssnValid")]
    [InlineData(VendorKind.Aus, "ausEngine,recommendation,findings")]
    public void Report_carries_the_normalized_shape_per_kind(
        VendorKind kind, string requiredKeys)
    {
        var report = StubVendorAdapter.Report(kind, "ext-1");
        foreach (var key in requiredKeys.Split(','))
            report.ContainsKey(key).ShouldBeTrue(
                $"report for {kind} is missing '{key}'");
    }

    [Fact]
    public async Task No_request_pii_reaches_receipt_or_report()
    {
        var order = Credit();
        var receipt = await _adapter.SubmitAsync(order, null);
        var result = await _adapter.PollAsync(receipt, null);

        var surface = JsonSerializer.Serialize(receipt)
            + JsonSerializer.Serialize(result);
        foreach (var pii in new[]
                 {
                     order.FirstName, order.LastName, order.Ssn,
                     order.Dob, order.Street, order.Zip
                 })
            surface.ShouldNotContain(pii);
    }
}
