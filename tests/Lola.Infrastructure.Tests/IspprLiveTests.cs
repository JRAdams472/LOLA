using System.Text.Json.Nodes;
using Lola.Application.Documents;
using Lola.Infrastructure.Documents;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// MPE-190 acceptance against the live ISPPr stack: a generate →
/// poll → presigned-download round trip through the REAL adapter and
/// wire (X-API-Key auth, Idempotency-Key replay, the status poll, and
/// the 302 → MinIO presigned URL the tenant route mints), plus the
/// admin-session provisioning chain the tenant pipeline calls.
[Collection("isppr")]
public sealed class IspprLiveTests(IspprLiveFixture fixture)
{
    /// The seed's demo invoice template — fields: title, total, due,
    /// items[]. Nothing PII-shaped crosses in a probe payload.
    private static DocPackageRequest Request(string key,
        string customerId, string templateId)
        => new(customerId, "lola-live-probe", key,
        [
            new DocPackageDocument(templateId, new JsonObject
            {
                ["title"] = "LOLA live probe",
                ["total"] = 12.50,
                ["due"] = "2026-12-01",
                ["items"] = new JsonArray(
                    new JsonObject
                    {
                        ["desc"] = "probe item", ["price"] = 12.50
                    })
            })
        ]);

    private static async Task<DocGeneration> AwaitTerminalAsync(
        IDocumentService adapter, string requestId)
    {
        for (var i = 0; i < 60; i++)
        {
            var gen = await adapter.GetStatusAsync(requestId);
            if (gen.Status is "completed" or "failed")
                return gen;
            await Task.Delay(1000);
        }
        throw new InvalidOperationException(
            $"ISPPr request {requestId} never reached a terminal state");
    }

    [Fact]
    public async Task Generate_poll_and_presigned_download_round_trip()
    {
        var adapter = fixture.Adapter();
        var gen = await adapter.GenerateAsync(
            Request($"live-{Guid.NewGuid():N}",
                fixture.CustomerId, "tmpl_invoice"));
        gen.RequestId.ShouldNotBeNullOrEmpty();

        var done = gen.Status == "completed"
            ? gen
            : await AwaitTerminalAsync(adapter, gen.RequestId);
        done.Status.ShouldBe("completed");
        done.Filename.ShouldNotBeNullOrEmpty();
        done.Filename.ShouldEndWith(".zip");

        // Fresh presigned URL minted per call — under S3 storage the
        // tenant route 302s to MinIO; dereference it to prove the
        // package bytes are real and downloadable.
        var url = await adapter.GetDownloadUrlAsync(gen.RequestId);
        url.ShouldStartWith("http");
        var bytes = await new HttpClient().GetByteArrayAsync(url);
        bytes.Length.ShouldBeGreaterThan(100);
        // ZIP magic — the package is a real artifact.
        bytes[0].ShouldBe((byte)'P');
        bytes[1].ShouldBe((byte)'K');
    }

    [Fact]
    public async Task Idempotent_replay_returns_the_original_request()
    {
        var adapter = fixture.Adapter();
        var key = $"idem-{Guid.NewGuid():N}";
        var first = await adapter.GenerateAsync(
            Request(key, fixture.CustomerId, "tmpl_invoice"));
        var replay = await adapter.GenerateAsync(
            Request(key, fixture.CustomerId, "tmpl_invoice"));
        replay.RequestId.ShouldBe(first.RequestId);
    }

    [Fact]
    public async Task Bad_api_key_fails_typed_nonretryable_auth()
    {
        var adapter = fixture.Adapter(apiKey: "isppr_not_a_real_key");
        var ex = await Should.ThrowAsync<DocAdapterException>(
            () => adapter.GenerateAsync(
                Request($"bad-{Guid.NewGuid():N}",
                    fixture.CustomerId, "tmpl_invoice")));
        ex.Kind.ShouldBe(DocErrorKind.Auth);
        ex.Retryable.ShouldBeFalse();
    }

    /// The provisioning chain the tenant pipeline runs: admin session
    /// → ensure client → ensure customer → mint scoped key → upload
    /// the template master → the new key generates against it. One
    /// assertion path proves every admin call shape end-to-end.
    [Fact]
    public async Task Admin_session_provisions_a_usable_tenant()
    {
        var admin = fixture.AdminClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var clientId = await admin.EnsureClientAsync(
            $"lola-probe-{suffix}");
        clientId.ShouldNotBeNullOrEmpty();
        var customerId = await admin.EnsureCustomerAsync(clientId,
            "LOLA Probe Customer", $"lola-probe-{suffix}");
        customerId.ShouldNotBeNullOrEmpty();

        var (keyId, plaintext) = await admin.CreateApiKeyAsync(
            clientId, "lola probe key", ["generate"]);
        keyId.ShouldNotBeNullOrEmpty();
        plaintext.ShouldStartWith("isppr_");

        const string template = "tmpl_lola_probe";
        var uploaded = await admin.UploadTemplateAsync(clientId,
            template,
            "<html><body><h1>{{heading}}</h1></body></html>");
        uploaded.ShouldBeTrue();
        // Re-upload 409s → false — provisioning is re-entrant.
        (await admin.UploadTemplateAsync(clientId, template,
            "<html><body><h1>{{heading}}</h1></body></html>"))
            .ShouldBeFalse();

        var adapter = fixture.Adapter(apiKey: plaintext);
        var gen = await adapter.GenerateAsync(new DocPackageRequest(
            customerId, "lola-provisioned-probe",
            $"prov-{Guid.NewGuid():N}",
            [new DocPackageDocument(template,
                new JsonObject { ["heading"] = "provisioned" })]));
        var done = gen.Status == "completed"
            ? gen
            : await AwaitTerminalAsync(adapter, gen.RequestId);
        done.Status.ShouldBe("completed");
    }

    /// Republish (MPE-191), live-verified: a missing master
    /// self-heals as v1, an existing name bumps the next version,
    /// and a repeat call just publishes again — never errors.
    [Fact]
    public async Task Republish_bumps_versions_and_repeats_safely()
    {
        var admin = fixture.AdminClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var clientId = await admin.EnsureClientAsync(
            $"lola-repub-{suffix}");
        var name = $"tmpl_repub_{suffix}";
        IReadOnlyDictionary<string, string> templates =
            new Dictionary<string, string>
            {
                [name] = "<html><body><h1>{{heading}}</h1></body></html>"
            };

        var v1 = await admin.RepublishTemplatesAsync(clientId, templates);
        v1[name].ShouldBe(1);
        var v2 = await admin.RepublishTemplatesAsync(clientId, templates);
        v2[name].ShouldBe(2);
        var v3 = await admin.RepublishTemplatesAsync(clientId, templates);
        v3[name].ShouldBe(3);
    }

    /// The erasure hook's mechanism, live-verified: a minted key
    /// generates fine, admin revoke lands, and the same key then
    /// fails generate with a typed nonretryable Auth (vendor 401).
    [Fact]
    public async Task Revoked_key_fails_auth_on_subsequent_use()
    {
        var admin = fixture.AdminClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var clientId = await admin.EnsureClientAsync(
            $"lola-revoke-{suffix}");
        var customerId = await admin.EnsureCustomerAsync(clientId,
            "LOLA Revoke Probe", $"lola-revoke-{suffix}");
        var (keyId, plaintext) = await admin.CreateApiKeyAsync(
            clientId, "lola revoke probe", ["generate"]);
        // The key is client-scoped — generation needs a master under
        // THIS client (the seeded tmpl_invoice belongs to cli_demo).
        (await admin.UploadTemplateAsync(clientId, $"tmpl_revk_{suffix}",
            "<html><body><h1>{{heading}}</h1></body></html>"))
            .ShouldBeTrue();

        var adapter = fixture.Adapter(apiKey: plaintext);
        var ok = await adapter.GenerateAsync(new DocPackageRequest(
            customerId, "lola-revoke-probe", $"rev-{suffix}",
            [new DocPackageDocument($"tmpl_revk_{suffix}",
                new JsonObject { ["heading"] = "pre-revoke" })]));
        ok.RequestId.ShouldNotBeNullOrEmpty();

        await admin.RevokeApiKeyAsync(keyId);

        var ex = await Should.ThrowAsync<DocAdapterException>(
            () => adapter.GenerateAsync(new DocPackageRequest(
                customerId, "lola-revoke-probe", $"rev2-{suffix}",
                [new DocPackageDocument($"tmpl_revk_{suffix}",
                    new JsonObject { ["heading"] = "post-revoke" })])));
        ex.Kind.ShouldBe(DocErrorKind.Auth);
        ex.Retryable.ShouldBeFalse();
    }
}
