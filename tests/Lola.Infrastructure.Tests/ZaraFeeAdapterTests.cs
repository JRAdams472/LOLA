using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lola.Application.Fees;
using Lola.Infrastructure.Fees;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// MPE-183 adapter proofs against a scripted HTTP surface (no live
/// Zara — that's ZaraLiveTests): bearer token from file/env with
/// expiry-aware re-read, `POST /api/evaluate/{env}` shape
/// (tenantId + PII-free scenario), EvalReport normalization into
/// FeeEvaluation, and typed error classification driving the outbox
/// retry policy.
public sealed class ZaraFeeAdapterTests : IDisposable
{
    private readonly string _tokenFile = Path.Combine(
        Path.GetTempPath(), $"zara-tok-{Guid.NewGuid():N}.jwt");

    private static readonly FeeScenario Scenario = new(
        new FeeLoanSection("purchase", "conventional", 350000m, 360),
        new FeePropertySection("IL", "62701", "Sangamon", 475000m,
            560000m),
        new FeeBorrowerSection(true, false, false));

    private ZaraFeeOptions Options(string? envToken = null) => new(
        "http://zara.test", "dev",
        "aa5ce000-0000-4000-8000-0000000000ac", _tokenFile, envToken);

    public void Dispose()
    {
        if (File.Exists(_tokenFile)) File.Delete(_tokenFile);
    }

    /// Unsigned JWT carrying only an `exp` — the provider decodes the
    /// claim for refresh timing; validation is the engine's job.
    private static string Jwt(long expUnix)
    {
        string B64(string s) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"RS256","kid":"k1"}""")}." +
               $"{B64($$"""{"iss":"zara-devtoken","exp":{{expUnix}}}""")}." +
               $"{B64("sig")}";
    }

    private void WriteToken(long? expUnix = null)
        => File.WriteAllText(_tokenFile,
            Jwt(expUnix ?? DateTimeOffset.UtcNow.AddHours(1)
                .ToUnixTimeSeconds()));

    private sealed class ScriptedHandler(
        params Func<HttpRequestMessage, HttpResponseMessage>[] script)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>>
            _script = new(script);
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(await request.Content!
                .ReadAsStringAsync(ct));
            var step = _script.Count > 0 ? _script.Dequeue()
                : throw new InvalidOperationException(
                    $"unscripted request: {request.RequestUri}");
            return step(request);
        }
    }

    private static HttpResponseMessage Json(
        HttpStatusCode code, string body) =>
        new(code)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json")
        };

    private const string ReportBody = """
        {"fees":[
           {"artifact":"origination-base","feeCode":"origination",
            "tridBucket":"A","base":900,
            "adjustments":[{"rule":"veteran-discount","op":"add",
                            "value":-500,"runningTotal":400}],
            "total":400},
           {"artifact":"recording","feeCode":"recording-fee",
            "tridBucket":"E","base":125,"adjustments":[],"total":125}],
         "total":525,"elapsedMs":0.07,
         "fingerprint":"2:2026-10-30T17:11:35Z"}
        """;

    [Fact]
    public async Task Evaluate_posts_scenario_and_normalizes_report()
    {
        WriteToken();
        var handler = new ScriptedHandler(
            _ => Json(HttpStatusCode.OK, ReportBody));
        var adapter = new ZaraFeeAdapter(
            new HttpClient(handler), Options());

        var eval = await adapter.EvaluateAsync(
            Scenario, new FeeRequestOptions());

        // Wire shape: tenantId + the PII-free scenario under one body.
        var req = handler.Requests.Single();
        req.RequestUri!.AbsolutePath.ShouldBe("/api/evaluate/dev");
        req.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        req.Headers.Authorization.Parameter.ShouldBe(
            File.ReadAllText(_tokenFile).Trim());
        var body = JsonNode.Parse(handler.Bodies.Single())!;
        body["tenantId"]!.GetValue<string>()
            .ShouldBe("aa5ce000-0000-4000-8000-0000000000ac");
        body["scenario"]!["loan"]!["amount"]!.GetValue<decimal>()
            .ShouldBe(350000m);
        // Vendor transform: Zara's PII guard denies `zip` — the
        // adapter omits it while state/county still cross.
        body["scenario"]!["property"]!["zip"].ShouldBeNull();
        body["scenario"]!["property"]!["state"]!.GetValue<string>()
            .ShouldBe("IL");
        body["scenario"]!["property"]!["county"]!.GetValue<string>()
            .ShouldBe("Sangamon");
        body["scenario"]!["borrower"]!["vaEligible"]!.GetValue<bool>()
            .ShouldBeTrue();

        // Normalization: tridBucket → Section, artifact → Label,
        // adjustments + fingerprint ride through.
        eval.Adapter.ShouldBe("zara");
        eval.Fingerprint.ShouldBe("2:2026-10-30T17:11:35Z");
        eval.Items.Count.ShouldBe(2);
        var orig = eval.Items[0];
        orig.FeeCode.ShouldBe("origination");
        orig.Section.ShouldBe("A");
        orig.Label.ShouldBe("origination-base");
        orig.Base.ShouldBe(900m);
        orig.Total.ShouldBe(400m);
        orig.Adjustments.Single().Rule.ShouldBe("veteran-discount");
        orig.Adjustments.Single().RunningTotal.ShouldBe(400m);
    }

    [Fact]
    public async Task Env_token_wins_over_file()
    {
        WriteToken();
        var handler = new ScriptedHandler(
            _ => Json(HttpStatusCode.OK, ReportBody));
        var adapter = new ZaraFeeAdapter(
            new HttpClient(handler), Options(envToken: "env-jwt"));

        await adapter.EvaluateAsync(Scenario, new FeeRequestOptions());
        handler.Requests.Single().Headers.Authorization!.Parameter
            .ShouldBe("env-jwt");
    }

    [Fact]
    public async Task Unauthorized_rereads_token_file_and_retries_once()
    {
        WriteToken();
        var handler = new ScriptedHandler(
            _ => Json(HttpStatusCode.Unauthorized, "{}"),
            _ => Json(HttpStatusCode.OK, ReportBody));
        var adapter = new ZaraFeeAdapter(
            new HttpClient(handler), Options());

        var eval = await adapter.EvaluateAsync(
            Scenario, new FeeRequestOptions());
        eval.Items.ShouldNotBeEmpty();
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Client_error_is_rejected_not_retryable()
    {
        WriteToken();
        var handler = new ScriptedHandler(
            _ => Json(HttpStatusCode.BadRequest,
                """{"error":"unknown field path"}"""));
        var adapter = new ZaraFeeAdapter(
            new HttpClient(handler), Options());

        var ex = await Should.ThrowAsync<FeeAdapterException>(() =>
            adapter.EvaluateAsync(Scenario, new FeeRequestOptions()));
        ex.Kind.ShouldBe(FeeErrorKind.Rejected);
        ex.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task Server_error_is_unavailable_retryable()
    {
        WriteToken();
        var handler = new ScriptedHandler(
            _ => Json(HttpStatusCode.InternalServerError, "{}"));
        var adapter = new ZaraFeeAdapter(
            new HttpClient(handler), Options());

        var ex = await Should.ThrowAsync<FeeAdapterException>(() =>
            adapter.EvaluateAsync(Scenario, new FeeRequestOptions()));
        ex.Kind.ShouldBe(FeeErrorKind.Unavailable);
        ex.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task Missing_token_file_is_an_auth_error()
    {
        var handler = new ScriptedHandler();
        var adapter = new ZaraFeeAdapter(
            new HttpClient(handler), Options());

        var ex = await Should.ThrowAsync<FeeAdapterException>(() =>
            adapter.EvaluateAsync(Scenario, new FeeRequestOptions()));
        ex.Kind.ShouldBe(FeeErrorKind.Auth);
        handler.Requests.ShouldBeEmpty(); // never hit the wire
    }

    [Fact]
    public async Task Unparseable_response_is_a_contract_error()
    {
        WriteToken();
        var handler = new ScriptedHandler(
            _ => Json(HttpStatusCode.OK, "not json {"));
        var adapter = new ZaraFeeAdapter(
            new HttpClient(handler), Options());

        var ex = await Should.ThrowAsync<FeeAdapterException>(() =>
            adapter.EvaluateAsync(Scenario, new FeeRequestOptions()));
        ex.Kind.ShouldBe(FeeErrorKind.Contract);
    }

    [Fact]
    public async Task Fee_codes_filter_limits_items()
    {
        WriteToken();
        var handler = new ScriptedHandler(
            _ => Json(HttpStatusCode.OK, ReportBody));
        var adapter = new ZaraFeeAdapter(
            new HttpClient(handler), Options());

        var eval = await adapter.EvaluateAsync(Scenario,
            new FeeRequestOptions(["origination"]));
        eval.Items.Select(i => i.FeeCode).ShouldBe(["origination"]);
    }
}
