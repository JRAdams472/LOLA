using System.Net;
using System.Text.Json.Nodes;
using Lola.Application.Vendors;
using Lola.Infrastructure.Vendors;
using Shouldly;
using Xunit;

namespace Lola.Infrastructure.Tests;

/// M25 P2 (MPE-316) — DU/LPA adapter contract tests against the
/// fixture corpus: wire shape out, normalized report back, typed
/// failures. No live credentials anywhere — the fake HTTP handler
/// serves the recorded fixture bytes.
public sealed class AusAdapterContractTests
{
    private sealed class FixtureHandler(string body, HttpStatusCode code
        = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(code)
            {
                Content = new StringContent(body)
            });
        }
    }

    private static string Fixture(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "Lola.slnx")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(
            new[] { dir!.FullName, "tests", "fixtures", "aus" }
                .Concat(parts).ToArray()));
    }

    [Fact]
    public async Task Du_adapter_parses_fixture_response_into_normalized_report()
    {
        var handler = new FixtureHandler(
            Fixture("du", "du-response-approve-eligible-synthetic.xml"));
        var adapter = new DuAdapter(new HttpClient(handler),
            "http://aus.test");
        var receipt = await adapter.SubmitAsync(
            new AusOrderRequest("app-1", 1, "<xml/>"),
            new VendorCredentials("key", null), CancellationToken.None);
        receipt.ExternalId.ShouldBe("DU-SYNTHETIC-CASE-0001");
        handler.LastRequest!.Content!.Headers.ContentType!.MediaType
            .ShouldBe("application/xml");
        handler.LastRequest.Headers.Authorization!.Scheme.ShouldBe("Bearer");

        var result = await adapter.PollAsync(receipt,
            new VendorCredentials("key", null), CancellationToken.None);
        result.ShouldNotBeNull();
        result!.Report["recommendation"]!.GetValue<string>()
            .ShouldBe("approve-eligible");
        result.Report["ausVendor"]!.GetValue<string>().ShouldBe("du");
        (result.Report["findings"] as JsonArray)!.Count.ShouldBe(2);
        result.Report["responseXml"]!.GetValue<string>()
            .ShouldContain("CodifiedFindings");
        (await adapter.PollAsync(receipt, null, CancellationToken.None))
            .ShouldBeNull();
    }

    [Fact]
    public async Task Lpa_adapter_parses_fixture_response()
    {
        var handler = new FixtureHandler(
            Fixture("lpa", "lpa-response-accept-synthetic.xml"));
        var adapter = new LpaAdapter(new HttpClient(handler),
            "http://aus.test");
        var receipt = await adapter.SubmitAsync(
            new AusOrderRequest("app-1", 1, "<xml/>"),
            new VendorCredentials("key", null), CancellationToken.None);
        receipt.ExternalId.ShouldBe("LPA-TXN-SYNTHETIC-0001");
        var result = await adapter.PollAsync(receipt, null,
            CancellationToken.None);
        result!.Report["recommendation"]!.GetValue<string>()
            .ShouldBe("accept");
        result.Report["ausVendor"]!.GetValue<string>().ShouldBe("lpa");
    }

    [Fact]
    public async Task Adapter_without_tenant_credentials_is_typed_auth_failure()
    {
        var adapter = new DuAdapter(new HttpClient(
            new FixtureHandler("")), "http://aus.test");
        var ex = await Should.ThrowAsync<VendorAdapterException>(() =>
            adapter.SubmitAsync(new AusOrderRequest("app-1", 1, "<xml/>"),
                null, CancellationToken.None));
        ex.Kind.ShouldBe(VendorErrorKind.Auth);
    }

    [Fact]
    public async Task Adapter_malformed_response_yields_unable_to_evaluate_not_throw()
    {
        var handler = new FixtureHandler("<broken");
        var adapter = new DuAdapter(new HttpClient(handler),
            "http://aus.test");
        var receipt = await adapter.SubmitAsync(
            new AusOrderRequest("app-1", 1, "<xml/>"),
            new VendorCredentials("key", null), CancellationToken.None);
        var result = await adapter.PollAsync(receipt, null,
            CancellationToken.None);
        result!.Report["recommendation"]!.GetValue<string>()
            .ShouldBe("unable-to-evaluate");
    }

    [Fact]
    public async Task Adapter_server_error_is_retryable_unavailable()
    {
        var handler = new FixtureHandler("err",
            HttpStatusCode.InternalServerError);
        var adapter = new LpaAdapter(new HttpClient(handler),
            "http://aus.test");
        var ex = await Should.ThrowAsync<VendorAdapterException>(() =>
            adapter.SubmitAsync(new AusOrderRequest("app-1", 1, "<xml/>"),
                new VendorCredentials("key", null), CancellationToken.None));
        ex.Kind.ShouldBe(VendorErrorKind.Unavailable);
    }
}
