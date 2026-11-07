using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Lola.Application.Documents;
using Lola.Infrastructure.Documents;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// MPE-190 adapter proofs against a scripted HTTP surface (no live
/// ISPPr — that's IspprLiveTests): X-API-Key + Idempotency-Key on
/// generate, the ISPPr wire shape (customer_id/package_name/documents
/// []), status normalization (UPPER→lowercase), 302-Location surfacing
/// on download, and typed error classification driving the outbox
/// retry/DLQ policy.
public sealed class IspprDocumentAdapterTests
{
    private static readonly DocPackageRequest Request = new(
        "cust-1", "le-package-app-1", "doc-1",
        [new DocPackageDocument("loan-estimate",
            new JsonObject { ["loanAmount"] = 350000 })]);

    private sealed class FixedCredentials(string key)
        : IDocumentCredentialSource
    {
        public Task<string?> ApiKeyAsync(
            CancellationToken ct = default) => Task.FromResult<string?>(key);
    }

    private sealed class ScriptedHandler(
        params Func<HttpRequestMessage, HttpResponseMessage>[] script)
        : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage,
            HttpResponseMessage>> _script = new(script);
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? ""
                : await request.Content.ReadAsStringAsync(ct));
            var step = _script.Count > 0 ? _script.Dequeue()
                : throw new InvalidOperationException(
                    $"unscripted request: {request.RequestUri}");
            return step(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code,
        string body) => new(code)
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json")
        };

    private static IspprDocumentAdapter Adapter(
        ScriptedHandler handler) => new(new HttpClient(handler),
        new IspprDocOptions("http://isppr.test"),
        new FixedCredentials("test-api-key"));

    [Fact]
    public async Task Generate_posts_isppr_shape_with_key_and_idempotency()
    {
        var handler = new ScriptedHandler(_ => Json(
            HttpStatusCode.Created, """
            {"request_id":"req-1","status":"COMPLETED",
             "results":{"filename":"le-package-app-1_req-1.zip",
                        "download_url":"https://minio/x.zip?sig=1"}}
            """));
        var gen = await Adapter(handler).GenerateAsync(Request);

        gen.RequestId.ShouldBe("req-1");
        gen.Status.ShouldBe("completed");
        gen.Filename.ShouldBe("le-package-app-1_req-1.zip");

        var req = handler.Requests[0];
        req.RequestUri!.ToString().ShouldBe(
            "http://isppr.test/api/v1/packages/generate");
        req.Headers.GetValues("X-API-Key")
            .ShouldBe(["test-api-key"]);
        req.Headers.GetValues("Idempotency-Key").ShouldBe(["doc-1"]);
        handler.Bodies[0].ShouldContain("\"customer_id\":\"cust-1\"");
        handler.Bodies[0].ShouldContain("\"package_name\":\"le-package-app-1\"");
        handler.Bodies[0].ShouldContain("\"template_id\":\"loan-estimate\"");
        handler.Bodies[0].ShouldContain("\"loanAmount\":350000");
    }

    [Fact]
    public async Task Generate_202_normalizes_to_generating()
    {
        var handler = new ScriptedHandler(_ => Json(
            HttpStatusCode.Accepted,
            """
            {"request_id":"req-2","status":"QUEUED"}
            """));
        var gen = await Adapter(handler).GenerateAsync(Request);

        gen.RequestId.ShouldBe("req-2");
        gen.Status.ShouldBe("generating");
        gen.Filename.ShouldBeNull();
    }

    [Fact]
    public async Task Status_poll_normalizes_terminal_states()
    {
        var handler = new ScriptedHandler(
            _ => Json(HttpStatusCode.OK,
                """
                {"request_id":"req-2","status":"RENDERING"}
                """),
            _ => Json(HttpStatusCode.OK,
                """
                {"request_id":"req-2","status":"FAILED",
                 "error":"template render failed"}
                """));
        var adapter = Adapter(handler);

        var mid = await adapter.GetStatusAsync("req-2");
        mid.Status.ShouldBe("generating");
        var end = await adapter.GetStatusAsync("req-2");
        end.Status.ShouldBe("failed");
        end.Error.ShouldBe("template render failed");
    }

    [Fact]
    public async Task Download_surfaces_the_302_location()
    {
        var handler = new ScriptedHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers =
                {
                    Location = new Uri(
                        "https://minio.test/pkg.zip?X-Sig=abc")
                }
            });
        var adapter = new IspprDocumentAdapter(
            new HttpClient(handler)
            { /* factory sets AllowAutoRedirect
                                          on the real client */
            },
            new IspprDocOptions("http://isppr.test"),
            new FixedCredentials("test-api-key"));

        var url = await adapter.GetDownloadUrlAsync("req-1");
        url.ShouldBe("https://minio.test/pkg.zip?X-Sig=abc");
        handler.Requests[0].RequestUri!.ToString().ShouldBe(
            "http://isppr.test/api/v1/packages/req-1/download");
        handler.Requests[0].Headers.GetValues("X-API-Key")
            .ShouldBe(["test-api-key"]);
    }

    [Fact]
    public async Task No_credential_is_a_nonretryable_auth_failure()
    {
        var adapter = new IspprDocumentAdapter(
            new HttpClient(new ScriptedHandler()),
            new IspprDocOptions("http://isppr.test"),
            new FixedCredentials(null!));
        var ex = await Should.ThrowAsync<DocAdapterException>(
            () => adapter.GenerateAsync(Request));
        ex.Kind.ShouldBe(DocErrorKind.Auth);
        ex.Retryable.ShouldBeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DocErrorKind.Auth, false)]
    [InlineData(HttpStatusCode.Forbidden, DocErrorKind.Auth, false)]
    [InlineData(HttpStatusCode.BadRequest, DocErrorKind.Rejected, false)]
    [InlineData(HttpStatusCode.TooManyRequests, DocErrorKind.Rejected, true)]
    [InlineData(HttpStatusCode.InternalServerError, DocErrorKind.Unavailable, true)]
    public async Task Error_statuses_classify_for_retry_policy(
        HttpStatusCode code, DocErrorKind kind, bool retryable)
    {
        var handler = new ScriptedHandler(
            _ => Json(code, "{}"), _ => Json(code, "{}"),
            _ => Json(code, "{}"));
        var ex = await Should.ThrowAsync<DocAdapterException>(
            () => Adapter(handler).GenerateAsync(Request));
        ex.Kind.ShouldBe(kind);
        ex.Retryable.ShouldBe(retryable);
    }
}
