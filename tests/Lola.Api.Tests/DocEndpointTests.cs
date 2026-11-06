using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Identity;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-190 acceptance at the API surface: POST …/packages is 202 +
/// doc id, the request drains through DocOutboxProcessor to
/// completed, the registry list/detail read it, download 302s to a
/// fresh presigned URL and audits doc_download, a duplicate request
/// returns the original doc id, borrowers are forbidden from POST,
/// and le-package 409s without lock + done fee eval. The whole e2e
/// runs on AltStubDocumentService — the swap is interface-level.
public sealed class DocEndpointTests(LolaApiFactory factory)
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
            new { tenantId = tenant, slug = tenant, displayName = "Doc Co" });
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        // Vendor binding the provisioning step would normally write.
        await factory.Services.GetRequiredService<ITenantRegistry>()
            .SetIspprIdsAsync(tenant, $"cli-{tenant}", $"cust-{tenant}");
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(
                factory, tenant, LolaRole.LoanOfficer));
        return (tenant, officer);
    }

    private static async Task<string> ServiceTokenAsync(HttpClient client)
    {
        if (_serviceToken is not null) return _serviceToken;
        var resp = await client.PostAsync("/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-svc",
                ["client_secret"] = "svc-secret-value"
            }));
        return _serviceToken =
            (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }

    private async Task<string> DraftAsync(HttpClient officer,
        string base_)
    {
        var draft = await officer.PostAsJsonAsync(base_, new { });
        draft.StatusCode.ShouldBe(HttpStatusCode.OK);
        var appId = (await draft.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        var patch = await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new
            {
                patch = new Dictionary<string, string>
                {
                    ["party[0].first_name"] = "Ada",
                    ["party[0].last_name"] = "Lovelace",
                    ["party[0].email"] = "ada@example.com",
                    ["party[0].address[0].street"] = "1 Analytical Way",
                    ["party[0].address[0].city"] = "Springfield",
                    ["party[0].address[0].state"] = "IL",
                    ["party[0].address[0].zip"] = "62701",
                    ["party[0].employment[0].employer_name"] = "Engine Co",
                    ["loan_purpose"] = "purchase",
                    ["loan.product_type"] = "conventional",
                    ["loan.base_amount"] = "350000",
                    ["loan_amount"] = "350000",
                    ["loan_term_months"] = "360",
                    ["subject_property.street"] = "42 Babbage Rd",
                    ["subject_property.city"] = "Springfield",
                    ["subject_property.state"] = "IL",
                    ["subject_property.zip"] = "62701",
                    ["subject_property.county"] = "Sangamon",
                    ["subject_property.est_value"] = "560000",
                    ["subject_property.purchase_price"] = "475000",
                    ["declaration.intent_to_occupy"] = "primary",
                }
            });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);
        return appId;
    }

    /// Price → drain → lock → fee → drain: the two le-package
    /// prerequisites against the alt stubs.
    private async Task LockAndFeeAsync(string tenant,
        HttpClient officer, string base_, string appId)
    {
        var registry = factory.Services
            .GetRequiredService<ITenantRegistry>();
        var rec = await registry.FindByIdAsync(tenant);

        var price = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/price", new { });
        price.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.Services
            .GetRequiredService<Lola.Api.Pricing.PricingOutboxProcessor>()
            .DrainTenantAsync(rec!);
        var evals = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/pricing");
        var evalId = evals.GetProperty("evalId").GetString();

        var locked = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/lock",
            new
            {
                evalId,
                productCode = "alt-30",
                rate = 5.99m,
                lockTermDays = 30
            });
        locked.StatusCode.ShouldBe(HttpStatusCode.OK);

        var fee = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/fees", new { });
        fee.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.Services
            .GetRequiredService<Lola.Api.Fees.FeeOutboxProcessor>()
            .DrainTenantAsync(rec!);
    }

    [Fact]
    public async Task Le_package_full_arc_to_presigned_download()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);
        await LockAndFeeAsync(tenant, officer, base_, appId);

        var post = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/packages", new { kind = "le-package" });
        post.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var docId = (await post.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("docId").GetString()!;

        var registry = factory.Services
            .GetRequiredService<ITenantRegistry>();
        var rec = await registry.FindByIdAsync(tenant);
        var drained = await factory.Services
            .GetRequiredService<Lola.Api.Documents.DocOutboxProcessor>()
            .DrainTenantAsync(rec!);
        drained.ShouldBe(1);

        var list = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/documents");
        list.GetArrayLength().ShouldBe(1);
        list[0].GetProperty("status").GetString()
            .ShouldBe("completed");
        list[0].GetProperty("filename").GetString()
            .ShouldBe("le-package-" + appId + "_alt.zip");

        var detail = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/documents/{docId}");
        detail.GetProperty("docId").GetString().ShouldBe(docId);
        detail.GetProperty("kind").GetString().ShouldBe("le-package");

        // The factory's default client follows redirects — a real
        // caller gets the 302 and dereferences the presigned URL
        // itself, so assert the raw response on a no-redirect client.
        var noRedirect = factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing
                .WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });
        noRedirect.DefaultRequestHeaders.Authorization =
            officer.DefaultRequestHeaders.Authorization;
        var dl = await noRedirect.GetAsync(
            $"{base_}/{appId}/documents/{docId}/download");
        dl.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        dl.Headers.Location!.ToString().ShouldStartWith(
            "https://alt-stub.local/dl/alt-doc-");

        var audit = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/audit");
        var actions = audit.EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()).ToList();
        actions.ShouldContain("doc_generate");
        actions.ShouldContain("doc_download");
    }

    [Fact]
    public async Task Duplicate_package_request_returns_same_doc()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);
        await LockAndFeeAsync(tenant, officer, base_, appId);

        var a = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/packages", new { kind = "le-package" });
        var b = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/packages", new { kind = "le-package" });
        var aId = (await a.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("docId").GetString();
        var bBody = await b.Content.ReadFromJsonAsync<JsonElement>();
        bBody.GetProperty("docId").GetString().ShouldBe(aId);
        bBody.GetProperty("deduplicated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Le_package_409s_without_lock_or_fees()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);

        var post = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/packages", new { kind = "le-package" });
        post.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Adverse_action_enqueues_without_lock_or_fees()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);

        var post = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/packages",
            new
            {
                kind = "adverse-action",
                reasonCodes = new[] { "credit-history" }
            });
        post.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var bad = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/packages",
            new
            {
                kind = "adverse-action",
                reasonCodes = new[] { "invented-reason" }
            });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Borrower_cannot_generate_packages()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);

        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(
                factory, tenant, LolaRole.Borrower));
        var post = await borrower.PostAsJsonAsync(
            $"{base_}/{appId}/packages", new { kind = "le-package" });
        post.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task No_vendor_binding_is_a_409()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Nodoc" });
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(
                factory, tenant, LolaRole.LoanOfficer));
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftAsync(officer, base_);

        var post = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/packages",
            new
            {
                kind = "adverse-action",
                reasonCodes = new[] { "credit-history" }
            });
        post.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
