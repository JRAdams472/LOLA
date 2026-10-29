using System.Net;
using System.Text;
using Lola.Application.Pricing;
using Lola.Infrastructure.Pricing;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// MPE-130 adapter proofs against a scripted HTTP surface (no live
/// MPEx — that's MpexLiveTests): Dex token fetch + expiry cache,
/// eligibility→pricing normalization into PricingEvaluation, and
/// typed error classification driving the outbox retry policy.
public sealed class MpexPricingAdapterTests
{
    private static readonly MpexPricingOptions Options = new(
        "http://mpex.test", "http://dex.test/token",
        "mpex-svc", "secret", null);

    private static readonly PricingScenario Scenario = new(
        new LoanSection("purchase", "conventional", 350000m, 360,
            "fixed", null, 75m, 75m, 1),
        new PropertySection("sfr", "primary", "IL", 400000m, null),
        new BorrowerSection(720, "FICO", false, false),
        new ScenarioSection(30, null));

    private sealed class ScriptedHandler(
        params Func<HttpRequestMessage, HttpResponseMessage>[] script)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>>
            _script = new(script);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var step = _script.Count > 0 ? _script.Dequeue()
                : throw new InvalidOperationException(
                    $"unscripted request: {request.RequestUri}");
            return Task.FromResult(step(request));
        }
    }

    private static HttpResponseMessage Json(
        HttpStatusCode code, string body) =>
        new(code)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json")
        };

    private static HttpResponseMessage Token(HttpRequestMessage _) =>
        Json(HttpStatusCode.OK,
            """{"access_token":"tok-1","expires_in":3600}""");

    private const string EligibilityBody = """
        {"revision":"2026-10-01T00:00:00Z",
         "eligible":[
           {"product":{"code":"conv-30","name":"Conventional 30"},
            "eligible":true,"reasons":[]}],
         "ineligible":[
           {"product":{"code":"fha-30","name":"FHA 30"},
            "eligible":false,"reasons":["fico below 660"]}],
         "elapsedMs":1.2}
        """;

    private const string PricingBody = """
        {"revision":"2026-10-01T00:00:00Z",
         "priced":[
           {"product":{"code":"conv-30","name":"Conventional 30"},
            "rateSheet":"agency-conforming",
            "lines":[
              {"rate":6.5,"lockTermDays":30,"basePrice":99.5,
               "adjustmentTotal":-0.25,"price":99.25},
              {"rate":6.25,"lockTermDays":45,"basePrice":99.0,
               "adjustmentTotal":-0.25,"price":98.75}]}],
         "unpriced":[{"product":{"code":"va-30"},
                      "reason":"no sheet for revision"}],
         "elapsedMs":0.9}
        """;

    [Fact]
    public async Task Evaluates_eligibility_then_prices_eligible_only()
    {
        var handler = new ScriptedHandler(Token,
            _ => Json(HttpStatusCode.OK, EligibilityBody),
            req =>
            {
                var body = req.Content!.ReadAsStringAsync().Result;
                body.ShouldContain("\"conv-30\"");
                body.ShouldNotContain("\"fha-30\""); // ineligible never priced
                body.ShouldNotContain("firstName");   // PII-free egress
                return Json(HttpStatusCode.OK, PricingBody);
            });
        var adapter = new MpexPricingAdapter(
            new HttpClient(handler), Options);

        var eval = await adapter.EvaluateAsync(
            Scenario, new PricingRequestOptions());

        eval.Adapter.ShouldBe("mpex");
        eval.Products.Count.ShouldBe(2);
        var conv = eval.Products.Single(p => p.ProductCode == "conv-30");
        conv.Eligible.ShouldBeTrue();
        conv.Offers.Count.ShouldBe(2);
        conv.Offers[0].Rate.ShouldBe(6.5m);
        conv.Offers[0].Price.ShouldBe(99.25m);
        conv.Offers[0].Margin.ShouldBe(-0.25m);
        conv.Offers[0].LockTermDays.ShouldBe(30);
        conv.Offers[0].InvestorName.ShouldBe("agency-conforming");
        var fha = eval.Products.Single(p => p.ProductCode == "fha-30");
        fha.Eligible.ShouldBeFalse();
        fha.IneligibilityReasons.ShouldContain("fico below 660");
        fha.Offers.ShouldBeEmpty();
    }

    [Fact]
    public async Task Token_caches_across_calls()
    {
        var tokenCalls = 0;
        var handler = new ScriptedHandler(
            _ =>
            {
                tokenCalls++; return Json(HttpStatusCode.OK,
                """{"access_token":"tok","expires_in":3600}""");
            },
            _ => Json(HttpStatusCode.OK, EligibilityBody),
            _ => Json(HttpStatusCode.OK, PricingBody),
            _ => Json(HttpStatusCode.OK, EligibilityBody),
            _ => Json(HttpStatusCode.OK, PricingBody));
        var adapter = new MpexPricingAdapter(
            new HttpClient(handler), Options);

        await adapter.EvaluateAsync(Scenario, new PricingRequestOptions());
        await adapter.EvaluateAsync(Scenario, new PricingRequestOptions());
        tokenCalls.ShouldBe(1);
        handler.Requests.Count(r => r.Headers.Authorization
            ?.Parameter == "tok").ShouldBe(4);
    }

    [Fact]
    public async Task Unauthorized_invalidates_token_and_retries_once()
    {
        var tokens = 0;
        var handler = new ScriptedHandler(
            _ =>
            {
                tokens++; return Json(HttpStatusCode.OK,
                """{"access_token":"stale","expires_in":3600}""");
            },
            _ => Json(HttpStatusCode.Unauthorized, "{}"),
            _ =>
            {
                tokens++; return Json(HttpStatusCode.OK,
                """{"access_token":"fresh","expires_in":3600}""");
            },
            _ => Json(HttpStatusCode.OK, EligibilityBody),
            _ => Json(HttpStatusCode.OK, PricingBody));
        var adapter = new MpexPricingAdapter(
            new HttpClient(handler), Options);

        var eval = await adapter.EvaluateAsync(
            Scenario, new PricingRequestOptions());
        eval.Products.ShouldNotBeEmpty();
        tokens.ShouldBe(2);
    }

    [Fact]
    public async Task Client_error_is_rejected_not_retryable()
    {
        var handler = new ScriptedHandler(Token,
            _ => Json(HttpStatusCode.BadRequest,
                """{"error":"invalid loan model"}"""));
        var adapter = new MpexPricingAdapter(
            new HttpClient(handler), Options);

        var ex = await Should.ThrowAsync<PricingAdapterException>(() =>
            adapter.EvaluateAsync(Scenario, new PricingRequestOptions()));
        ex.Kind.ShouldBe(PricingErrorKind.Rejected);
        ex.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task Server_error_is_unavailable_retryable()
    {
        var handler = new ScriptedHandler(Token,
            _ => Json(HttpStatusCode.InternalServerError, "{}"));
        var adapter = new MpexPricingAdapter(
            new HttpClient(handler), Options);

        var ex = await Should.ThrowAsync<PricingAdapterException>(() =>
            adapter.EvaluateAsync(Scenario, new PricingRequestOptions()));
        ex.Kind.ShouldBe(PricingErrorKind.Unavailable);
        ex.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task Eligibility_only_skips_the_pricing_call()
    {
        var handler = new ScriptedHandler(Token,
            _ => Json(HttpStatusCode.OK, EligibilityBody));
        var adapter = new MpexPricingAdapter(
            new HttpClient(handler), Options);

        var eval = await adapter.EvaluateAsync(Scenario,
            new PricingRequestOptions(EligibilityOnly: true));
        eval.Products.Count.ShouldBe(2);
        eval.Products.All(p => p.Offers.Count == 0).ShouldBeTrue();
        handler.Requests.Count(r =>
            r.RequestUri!.AbsolutePath.Contains("/pricing"))
            .ShouldBe(0);
    }
}
