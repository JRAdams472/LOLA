using Lola.Application.Documents;
using Lola.Infrastructure.Documents;
using Shouldly;
using System.Text.Json.Nodes;

namespace Lola.Infrastructure.Tests;

/// MPE-189 stub-adapter proofs: the default document provider returns
/// deterministic COMPLETED packages keyed on the idempotency key, so
/// the seam and (Phase 2) outbox/registry flow are exercisable without
/// ISPPr. `DOC_ADAPTER=isppr` fails loudly at boot until the real
/// adapter lands — the swap is interface-level, no core change.
public sealed class StubDocumentServiceTests
{
    private readonly StubDocumentService _svc = new();

    private static DocPackageRequest Request(string key = "doc-1") =>
        new("cust-1", "le-package", key,
            [new DocPackageDocument("loan-estimate", new JsonObject())]);

    [Fact]
    public async Task Generate_returns_a_completed_package()
    {
        var gen = await _svc.GenerateAsync(Request());

        gen.Status.ShouldBe("completed");
        gen.RequestId.ShouldStartWith("stub-");
        gen.Filename.ShouldNotBeNull();
        gen.Filename.ShouldEndWith(".zip");
    }

    [Fact]
    public async Task Generate_is_idempotent_on_the_key()
    {
        var a = await _svc.GenerateAsync(Request("doc-same"));
        var b = await _svc.GenerateAsync(Request("doc-same"));
        var c = await _svc.GenerateAsync(Request("doc-different"));

        a.RequestId.ShouldBe(b.RequestId);
        a.RequestId.ShouldNotBe(c.RequestId);
    }

    [Fact]
    public async Task Poll_and_download_match_the_generated_request()
    {
        var gen = await _svc.GenerateAsync(Request());
        var status = await _svc.GetStatusAsync(gen.RequestId);
        var url = await _svc.GetDownloadUrlAsync(gen.RequestId);

        status.Status.ShouldBe("completed");
        status.RequestId.ShouldBe(gen.RequestId);
        url.ShouldContain(gen.RequestId);
    }
}
