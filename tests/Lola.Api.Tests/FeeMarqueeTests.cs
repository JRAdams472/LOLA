using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Identity;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-185 marquee — the full M8 arc in one walk, provisioned by
/// the platform operator: submit → price eval → fee eval →
/// LE-grouped sections → override → re-eval preserves → audit
/// spine (price_eval / fee_eval / fee_override).
public sealed class FeeMarqueeTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
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
    public async Task Full_fee_arc()
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
            new { tenantId = tenant, slug = tenant, displayName = "FeeMq" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant,
                LolaRole.LoanOfficer));
        var base_ = $"/api/tenants/{tenant}/applications";
        var rec = await factory.Services
            .GetRequiredService<ITenantRegistry>()
            .FindByIdAsync(tenant);

        // 2. Draft → full 1003 patch → submit.
        var appId = (await (await officer.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        (await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new { patch = Patch })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/submit", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // 3. Price → 202 → drain → done (the M7 arc precedes fees).
        (await officer.PostAsJsonAsync($"{base_}/{appId}/price",
            new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await factory.Services
            .GetRequiredService<Lola.Api.Pricing.PricingOutboxProcessor>()
            .DrainTenantAsync(rec!);
        (await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/pricing")).GetProperty("status")
            .GetString().ShouldBe("done");

        // 4. Fee eval → 202 → drain → LE-grouped sections.
        (await officer.PostAsJsonAsync($"{base_}/{appId}/fees",
            new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var feeProcessor = factory.Services
            .GetRequiredService<Lola.Api.Fees.FeeOutboxProcessor>();
        await feeProcessor.DrainTenantAsync(rec!);
        var fees = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        fees.GetProperty("evals")[0].GetProperty("status").GetString().ShouldBe("done");
        var sections = fees.GetProperty("evals")[0].GetProperty("sections");
        sections.GetArrayLength().ShouldBeGreaterThan(0);
        var line = sections[0].GetProperty("items")[0];
        var itemId = line.GetProperty("itemId").GetString()!;
        var computed = line.GetProperty("computed").GetDecimal();

        // 5. Override the first line — effective total shifts.
        (await officer.PatchAsJsonAsync(
            $"{base_}/{appId}/fees/{itemId}",
            new { amount = 500m, reason = "marquee correction" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        after.GetProperty("evals")[0].GetProperty("sections")[0].GetProperty("items")[0]
            .GetProperty("effective").GetDecimal().ShouldBe(500m);
        after.GetProperty("evals")[0].GetProperty("effectiveTotal").GetDecimal()
            .ShouldBeLessThan(after.GetProperty("evals")[0].GetProperty("computedTotal")
                .GetDecimal() + (computed - 500m) + 500m);

        // 6. Re-eval → override survives by feeCode+section.
        (await officer.PostAsJsonAsync($"{base_}/{appId}/fees",
            new { feeCodes = new[] { "origination", "recording" } }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await feeProcessor.DrainTenantAsync(rec!);
        var reeval = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        reeval.GetProperty("evals")[0].GetProperty("sections")[0].GetProperty("items")[0]
            .GetProperty("effective").GetDecimal().ShouldBe(500m);
        reeval.GetProperty("evals")[0].GetProperty("staleOverrides").GetArrayLength()
            .ShouldBe(0);
        var history = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees/history");
        history.GetArrayLength().ShouldBe(2);

        // 7. Audit spine — every M8 transition attributed.
        foreach (var action in new[] { "price_eval", "fee_eval",
            "fee_override" })
        {
            var events = await officer.GetFromJsonAsync<JsonElement>(
                $"/api/tenants/{tenant}/audit?action={action}");
            events.GetArrayLength().ShouldBeGreaterThan(0);
        }
        (await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/audit?action=fee_eval"))
            .GetArrayLength().ShouldBe(2); // eval + re-eval
    }
}
