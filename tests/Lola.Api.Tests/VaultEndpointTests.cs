using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-200 acceptance at the API surface: POST …/uploads streams a
/// multipart document through the sealing cipher into quarantine —
/// the bytes StubObjectVault actually stores are LVL1 ciphertext,
/// never the upload's plaintext — and returns 202. The registry
/// list/detail read it back, byte+classification dedupe returns the
/// original doc without a second object, the allowlist rejects bad
/// classes (400) and disallowed bytes (415), a stream over the cap
/// is 413 with nothing left in quarantine, and borrower-own scoping
/// holds on every route.
public sealed class VaultEndpointTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private static string? _serviceToken;

    private async Task<(string tenant, HttpClient officer)> SetupAsync()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        var made = await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Vault Co" });
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(
                factory, tenant, LolaRole.LoanOfficer));
        return (tenant, officer);
    }

    private static async Task<string> ServiceTokenAsync(HttpClient client)
    {
        if (_serviceToken is not null) return _serviceToken;
        var resp = await PkceLogin.PostWithRetryAsync(client, "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-svc",
                ["client_secret"] = "svc-secret-value"
            });
        return _serviceToken =
            (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }

    private async Task<string> DraftAsync(HttpClient user, string base_)
    {
        var draft = await user.PostAsJsonAsync(base_, new { });
        draft.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await draft.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
    }

    /// A minimal but real PDF — `%PDF-` magic + a unique marker so the
    /// ciphertext assertion can prove the plaintext is nowhere in the
    /// stored object.
    private static byte[] PdfBytes(string marker, int padTo = 0)
    {
        var body = System.Text.Encoding.ASCII.GetBytes(
            $"%PDF-1.4\n% {marker}\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n");
        if (body.Length >= padTo) return body;
        var padded = new byte[padTo];
        body.CopyTo(padded, 0);
        return padded;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, string url, byte[] bytes,
        string fileName = "paystub.pdf",
        string contentType = "application/pdf")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new(contentType);
        form.Add(file, "file", fileName);
        return await client.PostAsync(url, form);
    }

    private Lola.Infrastructure.Vault.StubObjectVault Store()
        => (Lola.Infrastructure.Vault.StubObjectVault)factory.Services
            .GetRequiredService<Lola.Application.Vault.IObjectVault>();

    private byte[]? StoredBytes(string tenant, string key)
    {
        using var _ = TenantContext.Use(new TenantScope(tenant, "", null));
        return Store().ReadBytes(key);
    }

    [Fact]
    public async Task Upload_seals_to_quarantine_registers_and_audits()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);
        var marker = $"plaintext-marker-{Guid.NewGuid():N}";

        var post = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=paystub",
            PdfBytes(marker), fileName: "march-paystub.pdf");
        post.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await post.Content.ReadFromJsonAsync<JsonElement>();
        var docId = body.GetProperty("vaultDocId").GetString()!;
        body.GetProperty("status").GetString().ShouldBe("quarantined");
        body.GetProperty("deduplicated").GetBoolean().ShouldBeFalse();

        // The registry row carries sniffed type + plaintext size/hash
        // — the filename round-trips decrypted for authorized readers.
        var detail = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/uploads/{docId}");
        detail.GetProperty("classification").GetString()
            .ShouldBe("paystub");
        detail.GetProperty("filename").GetString()
            .ShouldBe("march-paystub.pdf");
        detail.GetProperty("contentType").GetString()
            .ShouldBe("application/pdf");
        detail.GetProperty("status").GetString()
            .ShouldBe("quarantined");
        detail.GetProperty("sizeBytes").GetInt64()
            .ShouldBeGreaterThan(0);
        detail.GetProperty("sha256").GetString()!
            .Length.ShouldBe(64);

        var list = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/uploads");
        list.GetArrayLength().ShouldBe(1);
        list[0].GetProperty("vaultDocId").GetString().ShouldBe(docId);

        // The ciphertext-at-rest invariant (MPE-73): what the object
        // vault holds is an LVL1 frame — magic header + ciphertext —
        // and the upload's plaintext marker is nowhere inside it.
        var raw = StoredBytes(tenant, $"quarantine/{docId}");
        raw.ShouldNotBeNull();
        raw[..4].ShouldBe("LVL1"u8.ToArray());
        System.Text.Encoding.ASCII.GetString(raw)
            .ShouldNotContain(marker);
        raw.AsSpan().IndexOf(PdfBytes(marker)).ShouldBe(-1);

        // The upload lands on the app audit spine — the filename
        // (PII-class) does not.
        var audit = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/audit");
        var actions = audit.EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()).ToList();
        actions.ShouldContain("vault_upload");
        audit.GetRawText().ShouldNotContain("march-paystub.pdf");
    }

    [Fact]
    public async Task Same_bytes_dedupe_without_a_second_object()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);
        var bytes = PdfBytes("dedupe-marker");

        var a = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=w2", bytes,
            fileName: "w2-2025.pdf");
        var b = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=w2", bytes,
            fileName: "w2-2025-copy.pdf");
        a.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        b.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var aId = (await a.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("vaultDocId").GetString();
        var bBody = await b.Content.ReadFromJsonAsync<JsonElement>();
        bBody.GetProperty("vaultDocId").GetString().ShouldBe(aId);
        bBody.GetProperty("deduplicated").GetBoolean().ShouldBeTrue();

        var list = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/uploads");
        list.GetArrayLength().ShouldBe(1);
        // The replay's fresh object was cleaned up — one key only.
        Store().KeysFor(tenant)
            .Count(k => k.StartsWith("quarantine/",
                StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Allowlist_gates_classification_and_bytes()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);

        // Unknown classification — 400 before bytes are even sniffed.
        (await UploadAsync(officer,
                $"{base_}/{appId}/uploads?classification=secret",
                PdfBytes("x")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Missing classification — 400.
        (await UploadAsync(officer,
                $"{base_}/{appId}/uploads", PdfBytes("x")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Real bytes are the judge: an MZ executable named .pdf is
        // 415, never quarantined.
        var exe = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 };
        (await UploadAsync(officer,
                $"{base_}/{appId}/uploads?classification=paystub",
                exe, fileName: "not-a-virus.pdf"))
            .StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);

        // A PNG named .pdf stores as image/png — declared claims are
        // hints, the sniffed magic is canonical.
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A,
            0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        var sniffed = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=id",
            png, fileName: "license.pdf");
        sniffed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var docId = (await sniffed.Content
            .ReadFromJsonAsync<JsonElement>())
            .GetProperty("vaultDocId").GetString();
        var detail = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/uploads/{docId}");
        detail.GetProperty("contentType").GetString()
            .ShouldBe("image/png");

        // Non-multipart bodies never reach the handler — 415 at the
        // endpoint guard.
        (await officer.PostAsJsonAsync(
                $"{base_}/{appId}/uploads?classification=paystub",
                new { }))
            .StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);

        // Nothing disallowed ever touched the vault.
        Store().KeysFor(tenant)
            .Count(k => k.StartsWith("quarantine/",
                StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task Oversize_upload_is_413_and_leaves_no_trace()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);

        // Factory cap is 128 KiB — a valid PDF past it is 413 mid-
        // stream, the partial object is deleted, and no row is left.
        var big = PdfBytes("big-marker", padTo: 256 << 10);
        var resp = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=bank-statement",
            big, fileName: "statement.pdf");
        resp.StatusCode.ShouldBe((HttpStatusCode)413);

        (await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/uploads")).GetArrayLength().ShouldBe(0);
        Store().KeysFor(tenant).ShouldBeEmpty();
    }

    [Fact]
    public async Task Borrower_uploads_and_reads_own_app_only()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";

        var user = await factory.SeedUserAsync(tenant,
            $"b-{tenant}@test.dev", PkceLogin.Password, LolaRole.Borrower);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant,
                LolaRole.Borrower,
                email: $"b-{tenant}@test.dev", seed: false));

        var ownAppId = await DraftAsync(borrower, base_);
        var otherAppId = await DraftAsync(officer, base_);

        // Borrower uploads to their own app — the primary M10 actor.
        var post = await UploadAsync(borrower,
            $"{base_}/{ownAppId}/uploads?classification=paystub",
            PdfBytes("borrower-doc"), fileName: "paystub.pdf");
        post.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var docId = (await post.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("vaultDocId").GetString()!;

        (await borrower.GetAsync($"{base_}/{ownAppId}/uploads"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await borrower.GetAsync(
                $"{base_}/{ownAppId}/uploads/{docId}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Cross-app fails closed — upload 403, list 403, a doc id on
        // the wrong app 404s rather than confirming existence.
        (await UploadAsync(borrower,
                $"{base_}/{otherAppId}/uploads?classification=paystub",
                PdfBytes("nope"), fileName: "nope.pdf"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await borrower.GetAsync($"{base_}/{otherAppId}/uploads"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await borrower.GetAsync(
                $"{base_}/{otherAppId}/uploads/{docId}"))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cross_tenant_path_never_reaches_the_handler()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);

        // The officer's token binds tenant — a different path tenant
        // 403s before the multipart body is consumed.
        (await UploadAsync(officer,
                $"/api/tenants/other-tenant/applications/{appId}/uploads?classification=paystub",
                PdfBytes("x"), fileName: "x.pdf"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
