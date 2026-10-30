using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Api.Pricing;
using Lola.Application.Identity;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-132 marquee — the full pricing arc in one walk, provisioned
/// by the platform operator: submit → price → worker completes →
/// lock → frozen 409 → unlock → asOf re-eval → audit spine.
public sealed class PricingMarqueeTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    // A submit-valid 1003 patch plus the scenario-determining fields
    // the rate lock freezes.
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
        ["party[0].credit_score"] = "720",
        ["party[0].credit_model"] = "fico",
    };

    [Fact]
    public async Task Full_pricing_arc()
    {
        // 1. Provision as the platform operator (lola-ops).
        var tenant = $"mq{Guid.NewGuid():N}"[..12];
        var ops = factory.CreateClient();
        var tok = await ops.PostAsync("/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-ops",
                ["client_secret"] = "ops-secret-value"
            }));
        ops.DefaultRequestHeaders.Authorization = new("Bearer",
            (await tok.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!);
        (await ops.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Marq" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant,
                LolaRole.LoanOfficer));
        var base_ = $"/api/tenants/{tenant}/applications";

        // 2. Draft → scenario patch → submit.
        var appId = (await (await officer.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        (await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new { patch = Patch })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/submit", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // 3. Price → 202 → worker drains → eval done with offers.
        var priced = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/price", new { });
        priced.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var rec = await factory.Services
            .GetRequiredService<ITenantRegistry>()
            .FindByIdAsync(tenant);
        await factory.Services
            .GetRequiredService<PricingOutboxProcessor>()
            .DrainTenantAsync(rec!);
        var eval = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/pricing");
        eval.GetProperty("status").GetString().ShouldBe("done");

        // 4. Lock the offered rate — frozen paths now reject 409.
        (await officer.PostAsJsonAsync($"{base_}/{appId}/lock",
            new { productCode = "alt-30", rate = 5.99m, lockTermDays = 30 }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var frozen = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/correct",
            new
            {
                patch = new Dictionary<string, string>
                { ["loan.base_amount"] = "360000" }
            });
        frozen.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // 5. Unlock with a reason — the correction now passes.
        (await officer.PostAsJsonAsync($"{base_}/{appId}/unlock",
            new { reason = "marquee re-shop" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/correct",
            new
            {
                patch = new Dictionary<string, string>
                { ["loan.base_amount"] = "360000" }
            }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // 6. asOf re-eval → a second eval row with `reval` kind.
        (await officer.PostAsJsonAsync($"{base_}/{appId}/price",
            new { asOf = "2020-01-01T00:00:00Z" }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.Services
            .GetRequiredService<PricingOutboxProcessor>()
            .DrainTenantAsync(rec!);
        var history = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/pricing/history");
        history.GetArrayLength().ShouldBe(2);
        history.EnumerateArray().ShouldContain(e =>
            e.GetProperty("asOf").GetString() ==
                "2020-01-01T00:00:00Z");
        var outbox = factory.Services
            .GetRequiredService<FakePricingRepository>().Outbox.Values;
        outbox.ShouldContain(r => r.Kind == "reval");

        // 7. Audit spine — every pricing transition attributed.
        foreach (var action in new[] { "price_eval", "rate_lock",
            "rate_unlock" })
        {
            var events = await officer.GetFromJsonAsync<JsonElement>(
                $"/api/tenants/{tenant}/audit?action={action}");
            events.GetArrayLength().ShouldBeGreaterThan(0);
        }
        var evals = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/audit?action=price_eval");
        evals.GetArrayLength().ShouldBe(2); // live + reval
    }
}
