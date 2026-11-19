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
        // Replay → same external id (vendor idempotency); the
        // received timestamp legitimately mints per call.
        first.ExternalId.ShouldBe(replay.ExternalId);
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

    /// MPE-230 regression — the receipt hash must cover the derived
    /// record's fields, not just the base `VendorOrderRequest` (STJ
    /// serializes the declared type: two fraud payloads once produced
    /// identical external ids → FindByExternalIdAsync collided on the
    /// second same-kind order → webhook 500s).
    [Fact]
    public async Task Submit_ids_differ_per_payload_within_a_kind()
    {
        var a = await _adapter.SubmitAsync(new FraudOrderRequest(
            "Marina", "Sample", "000-12-3456", "1990-04-01"), null);
        var b = await _adapter.SubmitAsync(new FraudOrderRequest(
            "Marina", "Sample", "000-98-7654", "1990-04-01"), null);

        b.ExternalId.ShouldNotBe(a.ExternalId);
    }

    /// MPE-230 — a `${VAR:-}` compose passthrough injects an EMPTY
    /// string when the env var is unset; that must mean poll mode,
    /// not push (otherwise SupportsPush closes the outbox at submit
    /// and orders park at `submitted` forever — no completion channel).
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_webhook_url_keeps_poll_mode(string? url)
        => new StubVendorAdapter(url).SupportsPush.ShouldBeFalse();

    [Fact]
    public void Set_webhook_url_enables_push_mode()
        => new StubVendorAdapter("http://vendor.test/hook")
            .SupportsPush.ShouldBeTrue();

    /// MPE-230 — push mode POSTs a spec-shaped signed completion to
    /// the configured ingress: `sha256=<hmac(secret, raw body)>`, body
    /// carries tenant/external_id/outcome/report (MPE-228 contract).
    [Fact]
    public async Task Push_mode_posts_a_signed_completion_to_the_ingress()
    {
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add("http://localhost:51993/");
        listener.Start();
        var seen = new TaskCompletionSource<(string Body, string? Sig)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using var reader = new System.IO.StreamReader(
                ctx.Request.InputStream!);
            var body = await reader.ReadToEndAsync();
            ctx.Response.StatusCode = 200;
            ctx.Response.Close();
            seen.SetResult((body,
                ctx.Request.Headers["X-Vendor-Signature"]));
        });

        var adapter = new StubVendorAdapter("http://localhost:51993/hook");
        var creds = new VendorCredentials("k", "wh-secret");
        var receipt = await adapter.SubmitAsync(
            new FraudOrderRequest("Marina", "Sample",
                "000-12-3456", "1990-04-01"), creds);

        var done = await Task.WhenAny(seen.Task,
            Task.Delay(TimeSpan.FromSeconds(10)));
        done.ShouldBe(seen.Task,
            "stub should POST the completion within dispatch latency");
        var (body, sig) = await seen.Task;

        var expected = "sha256=" + Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("wh-secret"),
                System.Text.Encoding.UTF8.GetBytes(body)))
            .ToLowerInvariant();
        sig.ShouldBe(expected);
        body.ShouldContain(receipt.ExternalId);
        body.ShouldContain("\"outcome\":\"completed\"");

        listener.Stop();
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
