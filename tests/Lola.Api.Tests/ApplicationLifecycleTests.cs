using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-110 acceptance: full intake lifecycle via API — draft → patch →
/// validation-fail (structured errors) → operator correction → submit →
/// in_review → decisioned — plus role scoping (borrower own-draft),
/// blind search, masking, and immutability of submitted snapshots.
public sealed class ApplicationLifecycleTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private static Dictionary<string, string> CompletePatch() => new()
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
        ["party[0].employment[0].start_date"] = "2020-01-15",
        ["party[0].employment[0].self_employed"] = "false",
        ["party[0].employment[0].monthly_income"] = "14200",
        ["asset[0].asset_type"] = "checking",
        ["asset[0].institution"] = "First Bank",
        ["asset[0].account_number"] = "8844-2211",
        ["asset[0].balance"] = "85000",
        ["liability[0].liability_type"] = "auto",
        ["liability[0].creditor"] = "Car Loans Inc",
        ["liability[0].monthly_payment"] = "410",
        ["liability[0].balance"] = "12000",
        ["declaration.intent_to_occupy"] = "yes",
        ["declaration.first_time_buyer"] = "false",
        ["declaration.property_will_be"] = "primary_residence",
        ["subject_property.street"] = "1 Prime Plaza",
        ["subject_property.city"] = "Springfield",
        ["subject_property.state"] = "IL",
        ["subject_property.zip"] = "62701",
        ["subject_property.est_value"] = "560000",
        ["subject_property.property_type"] = "single_family",
        ["subject_property.usage"] = "primary_residence",
        ["demographics.ethnicity"] = "not_provided",
        ["demographics.race"] = "not_provided",
        ["demographics.sex"] = "not_provided",
        ["loan.base_amount"] = "425000",
        ["loan.ltv"] = "75.9",
        ["loan.lien_position"] = "first",
        ["loan.product_type"] = "conventional",
    };

    private async Task<(string tenant, HttpClient officer, HttpClient svc)>
        ProvisionAndStaffAsync()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        var made = await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Lifecycle Co" });
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));
        return (tenant, officer, svc);
    }

    private static async Task<string> ServiceTokenAsync(
        HttpClient client, string clientId = "lola-svc", string secret = "svc-secret-value")
    {
        var resp = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret
            }));
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }

    [Fact]
    public async Task Full_lifecycle_draft_correct_submit_review_decision()
    {
        var (tenant, officer, svc) = await ProvisionAndStaffAsync();
        var base_ = $"/api/tenants/{tenant}/applications";

        // draft
        var created = await officer.PostAsJsonAsync(base_, new { });
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var appId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        // patch draft + unknown path refused
        (await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new { patch = new Dictionary<string, string> { ["loan_amount"] = "425000" } }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new { patch = new Dictionary<string, string> { ["bogus.path"] = "x" } }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // validation-fail submit → 422 structured errors
        var failed = await officer.PostAsJsonAsync($"{base_}/{appId}/submit", new { });
        failed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await failed.Content.ReadFromJsonAsync<JsonElement>();
        var errors = problem.GetProperty("errors");
        errors.EnumerateArray().Any(e =>
            e.GetProperty("path").GetString() == "party[0].ssn" &&
            e.GetProperty("code").GetString() == "required").ShouldBeTrue();

        // operator correction → submit succeeds
        (await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new { patch = CompletePatch() })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/submit", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // immutability: patch on submitted refused; correct → new version
        (await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new { patch = new Dictionary<string, string> { ["loan_amount"] = "1" } }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var corrected = await officer.PostAsJsonAsync($"{base_}/{appId}/correct",
            new { patch = new Dictionary<string, string> { ["party[0].ssn"] = "987654321" } });
        corrected.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await corrected.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("version").GetInt32().ShouldBeGreaterThanOrEqualTo(3);

        // masked read — tenant-scoped service sees no plaintext SSN
        await factory.ResetServiceClientSecretAsync(tenant, "svc-secret-value");
        var tsvc = factory.CreateClient();
        tsvc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(tsvc, $"svc-{tenant}"));
        var got = await tsvc.GetAsync($"{base_}/{appId}");
        got.StatusCode.ShouldBe(HttpStatusCode.OK);
        var doc = (await got.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("doc");
        doc.GetProperty("party")[0].GetProperty("ssn").GetString()!
            .ShouldStartWith("•••");

        // blind search + lifecycle transitions
        var search = await officer.GetAsync($"{base_}/search?field=ssn&value=987654321");
        (await search.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("applicationIds")[0].GetString().ShouldBe(appId);

        (await officer.PostAsJsonAsync($"{base_}/{appId}/transition",
            new { target = "in_review" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/transition",
            new { target = "submitted" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/transition",
            new { target = "decisioned" })).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Borrower_scope_is_own_draft_only()
    {
        var (tenant, officer, svc) = await ProvisionAndStaffAsync();
        var base_ = $"/api/tenants/{tenant}/applications";

        // officer draft attributed to a DIFFERENT borrower
        var created = await officer.PostAsJsonAsync(base_,
            new { borrowerUserId = "u-someoneelse" });
        var appId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        // borrower user + token
        var user = await factory.SeedUserAsync(tenant,
            $"b-{tenant}@test.dev", PkceLogin.Password, LolaRole.Borrower);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower,
                email: $"b-{tenant}@test.dev", seed: false));

        // cross-user read/patch → 403
        (await borrower.GetAsync($"{base_}/{appId}"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // own draft: create → attributed to borrower's sub → readable
        var own = await borrower.PostAsJsonAsync(base_, new { });
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        var ownId = (await own.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        var ownDoc = await borrower.GetAsync($"{base_}/{ownId}");
        ownDoc.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ownDoc.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("borrowerUserId").GetString().ShouldBe(user.Id);

        // staff-only actions → 403 for borrower
        (await borrower.GetAsync($"{base_}/search?field=ssn&value=123456789"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await borrower.PostAsJsonAsync($"{base_}/{ownId}/transition",
            new { target = "in_review" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
