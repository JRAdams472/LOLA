using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Identity;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-192 doc marquee — the M9 arc on the real stack: the ops client
/// provisions a tenant for real (OpenBao transit keys, keyed SQLCipher
/// file + migrations, live ISPPr client/customer/api-key/4 template
/// masters), then an officer walks submit → price → lock → fee →
/// le-package. The outbox drain generates against LIVE ISPPr, the
/// download route 302s to a MinIO presigned URL, and this test
/// dereferences it and asserts real ZIP bytes — the whole vendor
/// path is exercised, nothing doc-side is stubbed.
public sealed class DocMarqueeTests(DocMarqueeFactory factory)
    : IClassFixture<DocMarqueeFactory>
{
    private static readonly Dictionary<string, string> Patch = new()
    {
        ["loan_amount"] = "425000",
        ["loan_purpose"] = "purchase",
        ["amortization_type"] = "fixed",
        ["loan_term_months"] = "360",
        ["party[0].role"] = "borrower",
        ["party[0].first_name"] = "Ada",
        ["party[0].last_name"] = "Lovelace",
        ["party[0].ssn"] = "123456789",
        ["party[0].dob"] = "1985-12-10",
        ["party[0].email"] = "ada@example.com",
        ["party[0].phone"] = "+1-555-0100",
        ["party[0].address[0].street"] = "12 Analytical Way",
        ["party[0].address[0].city"] = "Springfield",
        ["party[0].address[0].state"] = "IL",
        ["party[0].address[0].zip"] = "62701",
        ["party[0].employment[0].employer_name"] = "Acme Engines",
        ["party[0].employment[0].monthly_income"] = "14200",
        ["party[0].employment[0].start_date"] = "2020-01-15",
        ["party[0].employment[0].self_employed"] = "false",
        ["asset[0].asset_type"] = "checking",
        ["asset[0].institution"] = "First Bank",
        ["asset[0].account_number"] = "8844",
        ["asset[0].balance"] = "85000",
        ["liability[0].liability_type"] = "auto",
        ["liability[0].creditor"] = "Car Co",
        ["liability[0].monthly_payment"] = "410",
        ["liability[0].balance"] = "12000",
        ["declaration.intent_to_occupy"] = "yes",
        ["declaration.first_time_buyer"] = "false",
        ["declaration.property_will_be"] = "primary_residence",
        ["declaration.va_eligible"] = "false",
        ["declaration.discount_points"] = "false",
        ["subject_property.street"] = "1 Prime Plaza",
        ["subject_property.city"] = "Springfield",
        ["subject_property.state"] = "IL",
        ["subject_property.zip"] = "62701",
        ["subject_property.county"] = "Sangamon",
        ["subject_property.est_value"] = "560000",
        ["subject_property.purchase_price"] = "475000",
        ["subject_property.property_type"] = "single_family",
        ["subject_property.usage"] = "primary_residence",
        ["demographics.ethnicity"] = "not_provided",
        ["demographics.race"] = "not_provided",
        ["demographics.sex"] = "not_provided",
        ["loan.base_amount"] = "425000",
        ["loan.ltv"] = "75.9",
        ["loan.lien_position"] = "first",
        ["loan.product_type"] = "conventional",
        ["party[0].credit_score"] = "720",
        ["party[0].credit_model"] = "fico",
    };

    [Fact]
    public async Task Full_doc_arc_against_live_isppr()
    {
        var tenant = $"mq{Guid.NewGuid():N}"[..12];

        // 1. Platform-operator provisioning — the WHOLE pipeline runs
        //    for real here: OpenBao transit keys, the keyed tenant
        //    file + migrations, the OIDC svc client, and the ISPPr
        //    binding (client → customer → generate key → 4 masters).
        var ops = factory.CreateClient();
        var tok = await PkceLogin.PostWithRetryAsync(ops, "/connect/token",
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-ops",
                ["client_secret"] = "ops-secret-value"
            });
        ops.DefaultRequestHeaders.Authorization = new("Bearer",
            (await tok.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!);
        (await ops.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "DocMq" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        // The vendor binding materialized on the registry row — client,
        // customer, minted key id, and the transit-wrapped key material.
        // The plaintext key never rested anywhere (invariant 1).
        var registry = factory.Services
            .GetRequiredService<ITenantRegistry>();
        var rec = await registry.FindByIdAsync(tenant);
        rec.ShouldNotBeNull();
        rec.Status.ShouldBe("active");
        rec.DbKeyWrapped.ShouldNotBeNullOrEmpty();
        rec.IspprClientId.ShouldNotBeNullOrEmpty();
        rec.IspprCustomerId.ShouldNotBeNullOrEmpty();
        rec.IspprKeyId.ShouldNotBeNullOrEmpty();
        rec.IspprKeyWrapped.ShouldNotBeNullOrEmpty();

        // 2. Officer draft → full 1003 patch → submit.
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant,
                LolaRole.LoanOfficer));
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = (await (await officer.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        (await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new { patch = Patch })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/submit", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // 3. Price → lock → fee — the le-package prerequisites.
        (await officer.PostAsJsonAsync($"{base_}/{appId}/price",
            new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.Services
            .GetRequiredService<Lola.Api.Pricing.PricingOutboxProcessor>()
            .DrainTenantAsync(rec);
        var evalId = (await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/pricing")).GetProperty("evalId").GetString();
        (await officer.PostAsJsonAsync($"{base_}/{appId}/lock",
            new
            {
                evalId,
                productCode = "alt-30",
                rate = 5.99m,
                lockTermDays = 30
            })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/fees",
            new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.Services
            .GetRequiredService<Lola.Api.Fees.FeeOutboxProcessor>()
            .DrainTenantAsync(rec);

        // 4. le-package → 202 + doc id, then poll-drain until the live
        //    engine finishes the render (three docs through Gotenberg —
        //    seconds, not ms; 3s polls, 2-minute ceiling).
        var post = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/packages", new { kind = "le-package" });
        post.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var docId = (await post.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("docId").GetString()!;

        var processor = factory.Services
            .GetRequiredService<Lola.Api.Documents.DocOutboxProcessor>();
        var deadline = DateTime.UtcNow.AddMinutes(2);
        string? status;
        for (status = "pending"; status is "pending" or "generating"
            && DateTime.UtcNow < deadline;)
        {
            await processor.DrainTenantAsync(rec);
            status = (await officer.GetFromJsonAsync<JsonElement>(
                    $"{base_}/{appId}/documents/{docId}"))
                .GetProperty("status").GetString();
            if (status is "pending" or "generating")
                await Task.Delay(TimeSpan.FromSeconds(3));
        }
        status.ShouldBe("completed");

        // 5. Registry reads — the real tenant file, newest first.
        var list = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/documents");
        list.GetArrayLength().ShouldBe(1);
        list[0].GetProperty("docId").GetString().ShouldBe(docId);
        var filename = list[0].GetProperty("filename").GetString();
        filename.ShouldNotBeNullOrEmpty();
        filename.ShouldEndWith(".zip");

        // 6. Download → 302 to a fresh MinIO presigned URL → the test
        //    dereferences it and proves real package bytes came back.
        var noRedirect = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });
        noRedirect.DefaultRequestHeaders.Authorization =
            officer.DefaultRequestHeaders.Authorization;
        var dl = await noRedirect.GetAsync(
            $"{base_}/{appId}/documents/{docId}/download");
        dl.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = dl.Headers.Location!.ToString();
        location.ShouldStartWith("http");
        var bytes = await new HttpClient().GetByteArrayAsync(location);
        bytes.Length.ShouldBeGreaterThan(100);
        bytes[0].ShouldBe((byte)'P'); // ZIP magic — a real artifact.
        bytes[1].ShouldBe((byte)'K');

        // 7. Audit spine — generation and retrieval both attributed
        //    on the application's spine, read back from the tenant file.
        var audit = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/audit");
        var actions = audit.EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()).ToList();
        actions.ShouldContain("doc_generate");
        actions.ShouldContain("doc_download");
    }
}
