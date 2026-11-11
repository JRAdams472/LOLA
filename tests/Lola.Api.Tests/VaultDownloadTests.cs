using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-202 acceptance at the API surface: the download mint 302s to a
/// ≤15-min capability URL whose signature is the credential, the
/// content route streams the decrypted bytes back, and every failure
/// mode — tampered, expired, cross-tenant replay, non-clean, wrong
/// app — refuses. The stub signer runs on a SettableTimeProvider so
/// the expiry case is real, not simulated.
public sealed class VaultDownloadTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private static string? _serviceToken;

    private async Task<(string tenant, HttpClient officer)> SetupAsync()
    {
        // Shared signer clock — an earlier test's advance must not
        // age this test's fresh mints.
        factory.Services.GetRequiredService<SettableTimeProvider>()
            .Reset();
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        var made = await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "V" });
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(
                factory, tenant, LolaRole.LoanOfficer));
        return (tenant, officer);
    }

    private static async Task<string> ServiceTokenAsync(HttpClient c)
    {
        if (_serviceToken is not null) return _serviceToken;
        var resp = await PkceLogin.PostWithRetryAsync(c, "/connect/token",
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

    private static async Task<string> DraftAsync(HttpClient user,
        string base_)
    {
        var draft = await user.PostAsJsonAsync(base_, new { });
        draft.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await draft.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
    }

    private static async Task<string> UploadAsync(HttpClient client,
        string url, string fileName = "march paystub's.pdf")
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(
            $"%PDF-1.4\n% download-{Guid.NewGuid():N}\n%%EOF\n");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new("application/pdf");
        form.Add(file, "file", fileName);
        var post = await client.PostAsync(url, form);
        post.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await post.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("vaultDocId").GetString()!;
    }

    /// Stage the scan verdict the worker would have produced — the
    /// hermetic suite keeps VAULT_SCAN_ENABLED off, so the row flips
    /// and the ciphertext is promoted by hand.
    private async Task PromoteAsync(string tenant, string docId)
    {
        using var _ = TenantContext.Use(new TenantScope(tenant, "", null));
        var store = factory.Services
            .GetRequiredService<IObjectVault>();
        await store.CopyAsync($"quarantine/{docId}", $"clean/{docId}");
        var repo = (FakeVaultDocRepository)factory.Services
            .GetRequiredService<IVaultDocRepository>();
        repo.Docs[docId] = repo.Docs[docId] with
        {
            Status = "clean",
            S3Key = $"clean/{docId}"
        };
    }

    private static async Task<string> MintAsync(HttpClient client,
        string url, HttpStatusCode expect = HttpStatusCode.Redirect)
    {
        var resp = await client.GetAsync(url);
        resp.StatusCode.ShouldBe(expect);
        return resp.Headers.Location?.OriginalString ?? "";
    }

    private async Task<List<string?>> AuditActionsAsync(HttpClient c,
        string base_, string appId)
    {
        var audit = await c.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/audit");
        return audit.EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()).ToList();
    }

    [Fact]
    public async Task Mint_302s_and_content_streams_decrypted_bytes()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);
        var plaintext = System.Text.Encoding.ASCII.GetBytes(
            $"%PDF-1.4\n% dl-{Guid.NewGuid():N}\n%%EOF\n");
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(plaintext);
        file.Headers.ContentType = new("application/pdf");
        form.Add(file, "file", "march paystub's.pdf");
        var post = await officer.PostAsync(
            $"{base_}/{appId}/uploads?classification=paystub", form);
        var docId = (await post.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("vaultDocId").GetString()!;
        await PromoteAsync(tenant, docId);

        var location = await MintAsync(officer,
            $"{base_}/{appId}/uploads/{docId}/download");
        location.ShouldContain("/api/vault-content/" + docId);
        location.ShouldContain($"tid={tenant}");
        location.ShouldContain("exp=");
        location.ShouldContain("sig=");

        var dl = await officer.GetAsync(location);
        dl.StatusCode.ShouldBe(HttpStatusCode.OK);
        dl.Content.Headers.ContentType!.MediaType
            .ShouldBe("application/pdf");
        (await dl.Content.ReadAsByteArrayAsync()).ShouldBe(plaintext);
        // RFC5987 filename* — the borrower's own name round-trips,
        // quote and all, with no header-injection surface.
        ContentDispositionHeaderValue.TryParse(
            dl.Content.Headers.ContentDisposition?.ToString(),
            out var cd).ShouldBeTrue();
        cd!.FileNameStar.Value.ShouldBe("march paystub's.pdf");

        (await AuditActionsAsync(officer, base_, appId))
            .ShouldContain("vault_download");
    }

    [Fact]
    public async Task Tampered_sig_or_exp_is_403()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);
        var docId = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=paystub");
        await PromoteAsync(tenant, docId);
        var location = await MintAsync(officer,
            $"{base_}/{appId}/uploads/{docId}/download");

        var tamperedSig = location.Replace("sig=", "sig=AAAA",
            StringComparison.Ordinal);
        (await officer.GetAsync(tamperedSig)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);

        var q = location.Split('?')[1];
        var parts = System.Web.HttpUtility.ParseQueryString(q);
        parts["exp"] =
            (long.Parse(parts["exp"]!) + 60).ToString();
        var uri = location.Split('?')[0] + "?" + parts;
        (await officer.GetAsync(uri)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Expired_signature_is_403()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);
        var docId = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=paystub");
        await PromoteAsync(tenant, docId);
        var location = await MintAsync(officer,
            $"{base_}/{appId}/uploads/{docId}/download");
        (await officer.GetAsync(location)).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        factory.Services.GetRequiredService<SettableTimeProvider>()
            .Advance(TimeSpan.FromMinutes(16));
        (await officer.GetAsync(location)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Cross_tenant_replay_is_403()
    {
        var (tenant, officer) = await SetupAsync();
        var (other, _) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);
        var docId = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=paystub");
        await PromoteAsync(tenant, docId);
        var location = await MintAsync(officer,
            $"{base_}/{appId}/uploads/{docId}/download");

        var replay = location.Replace($"tid={tenant}", $"tid={other}",
            StringComparison.Ordinal);
        (await officer.GetAsync(replay)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Non_clean_and_wrong_app_refuse_the_mint()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);
        var other = await DraftAsync(officer, base_);
        var docId = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=paystub");

        // Still quarantined — no scan promotion in the hermetic host.
        var mint = await officer.GetAsync(
            $"{base_}/{appId}/uploads/{docId}/download");
        mint.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Same doc id under another application — 404, never a leak.
        (await officer.GetAsync(
            $"{base_}/{other}/uploads/{docId}/download")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await officer.GetAsync(
            $"{base_}/{appId}/uploads/no-such-doc/download")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Two_mints_write_two_audit_events()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);
        var docId = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=paystub");
        await PromoteAsync(tenant, docId);
        await MintAsync(officer,
            $"{base_}/{appId}/uploads/{docId}/download");
        await MintAsync(officer,
            $"{base_}/{appId}/uploads/{docId}/download");
        (await AuditActionsAsync(officer, base_, appId))
            .Count(a => a == "vault_download").ShouldBe(2);
    }

    [Fact]
    public async Task Malformed_capability_params_are_403()
    {
        var (tenant, officer) = await SetupAsync();
        foreach (var url in new[]
        {
            "/api/vault-content/doc-1",
            "/api/vault-content/doc-1?tid=" + tenant,
            $"/api/vault-content/doc-1?tid={tenant}&exp=abc&sig=x",
            "/api/vault-content/doc-1?tid=no-such-tenant&exp=1&sig=x",
            $"/api/vault-content/doc-1?tid={tenant}&exp=1&sig=x"
        })
            (await officer.GetAsync(url)).StatusCode
                .ShouldBe(HttpStatusCode.Forbidden, url);
    }

    [Fact]
    public async Task List_is_newest_first()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);
        var first = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=paystub");
        var second = await UploadAsync(officer,
            $"{base_}/{appId}/uploads?classification=w2",
            fileName: "w2.pdf");
        var list = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/uploads");
        list.GetArrayLength().ShouldBe(2);
        list[0].GetProperty("vaultDocId").GetString().ShouldBe(second);
        list[1].GetProperty("vaultDocId").GetString().ShouldBe(first);
    }
}
