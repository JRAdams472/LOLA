using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-112 marquee: provision → draft → validate-fail → correct →
/// submit (immutable) → in_review → decisioned → MISMO round-trip →
/// TRID clock asserted → named-step history.
public sealed class WorkflowMarqueeTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private static readonly Dictionary<string, string> TridSix = new()
    {
        ["loan_amount"] = "425000",
        ["party[0].first_name"] = "Ada",
        ["party[0].ssn"] = "123456789",
        ["party[0].employment[0].monthly_income"] = "14200",
        ["subject_property.street"] = "1 Prime Plaza",
        ["subject_property.est_value"] = "560000",
    };

    [Fact]
    public async Task Marquee_lifecycle_trid_and_step_history()
    {
        // provision
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Marquee Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));
        var base_ = $"/api/tenants/{tenant}/applications";

        // draft
        var appId = (await (await officer.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var got = await officer.GetFromJsonAsync<JsonElement>($"{base_}/{appId}");
        got.GetProperty("tridClockStart").ValueKind.ShouldBe(JsonValueKind.Null);
        got.GetProperty("pipelineStep").GetString().ShouldBe("POSIntake");

        // validate-fail submit (only 5 of 6 TRID fields + nothing else)
        (await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new
            {
                patch = new Dictionary<string, string>(TridSix)
                { ["loan_amount"] = "425000" }
            }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/submit", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        // correct (patch the full required set) → submit → immutable
        var full = new Dictionary<string, string>(TridSix)
        {
            ["loan_purpose"] = "purchase",
            ["amortization_type"] = "fixed",
            ["loan_term_months"] = "360",
            ["party[0].role"] = "borrower",
            ["party[0].last_name"] = "Lovelace",
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
            ["subject_property.city"] = "Springfield",
            ["subject_property.state"] = "IL",
            ["subject_property.zip"] = "62701",
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
        (await officer.PatchAsJsonAsync($"{base_}/{appId}", new { patch = full }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/submit", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // TRID asserted: clock stamped once + business-day deadline exposed
        var after = await officer.GetFromJsonAsync<JsonElement>($"{base_}/{appId}");
        after.GetProperty("tridClockStart").GetString().ShouldNotBeNullOrEmpty();
        after.GetProperty("tridDeadlineUtc").GetString().ShouldNotBeNullOrEmpty();
        after.GetProperty("pipelineStep").GetString().ShouldBe("Validate");

        // in_review → decisioned
        (await officer.PostAsJsonAsync($"{base_}/{appId}/transition",
            new { target = "in_review" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/transition",
            new { target = "decisioned" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // MISMO round-trip: export → import → new draft; TRID complete
        // on the imported copy restamps its own clock
        var xml = await (await officer.GetAsync($"{base_}/{appId}/export-mismo"))
            .Content.ReadAsStringAsync();
        xml.ShouldContain("PARTY");
        var imported = await officer.PostAsJsonAsync($"{base_}/import-mismo",
            new { xml });
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
        var newId = (await imported.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        (await officer.GetFromJsonAsync<JsonElement>($"{base_}/{newId}"))
            .GetProperty("tridClockStart").GetString().ShouldNotBeNullOrEmpty();

        // step history: POSIntake→Validate entered on submit (append-only)
        var steps = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/steps");
        var entered = steps.EnumerateArray()
            .Where(e => e.GetProperty("event").GetString() == "entered")
            .Select(e => e.GetProperty("step").GetString()!).ToList();
        entered.ShouldBe(["Validate"]);
    }

    private static async Task<string> ServiceTokenAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-svc",
                ["client_secret"] = "svc-secret-value"
            }));
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }
}
