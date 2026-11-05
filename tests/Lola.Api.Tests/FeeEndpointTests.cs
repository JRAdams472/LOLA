using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Fees;
using Lola.Application.Identity;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-183 acceptance at the API surface: POST …/fees is 202 +
/// eval id, the eval drains through FeeOutboxProcessor to done,
/// GET …/fees + /history read the spine, a duplicate request
/// dedupes, and borrowers are forbidden. The whole e2e runs on
/// AltStubFeeService — the second implementation proving swap is
/// "implement one interface".
public sealed class FeeEndpointTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private async Task<(string tenant, HttpClient officer)> SetupAsync()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        var made = await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Fee Co" });
        made.StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(
                factory, tenant, LolaRole.LoanOfficer));
        return (tenant, officer);
    }

    private static async Task<string> ServiceTokenAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-svc",
                ["client_secret"] = "svc-secret-value"
            }));
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }

    private async Task<string> DraftWithScenarioAsync(
        HttpClient officer, string base_)
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
                    ["loan_purpose"] = "purchase",
                    ["loan.product_type"] = "conventional",
                    ["loan.base_amount"] = "350000",
                    ["loan_term_months"] = "360",
                    ["subject_property.state"] = "IL",
                    ["subject_property.zip"] = "62701",
                    ["subject_property.county"] = "Sangamon",
                    ["subject_property.est_value"] = "560000",
                    ["subject_property.purchase_price"] = "475000",
                    ["declaration.va_eligible"] = "true",
                    ["party[0].ssn"] = "123456789",
                }
            });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);
        return appId;
    }

    [Fact]
    public async Task Fees_enqueues_202_then_worker_completes()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithScenarioAsync(officer, base_);

        var eval = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/fees", new { });
        eval.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var evalId = (await eval.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("evalId").GetString()!;

        var pending = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        pending.GetProperty("status").GetString().ShouldBe("pending");
        pending.GetProperty("adapter").GetString().ShouldBe("alt-fee");

        // Drain the tenant's outbox — the fake repo + alt adapter run.
        var registry = factory.Services
            .GetRequiredService<ITenantRegistry>();
        var rec = await registry.FindByIdAsync(tenant);
        var drained = await factory.Services
            .GetRequiredService<Lola.Api.Fees.FeeOutboxProcessor>()
            .DrainTenantAsync(rec!);
        drained.ShouldBe(1);

        var done = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        done.GetProperty("status").GetString().ShouldBe("done");
        done.GetProperty("result")
            .GetProperty("adapter").GetString().ShouldBe("alt-fee");
        var items = done.GetProperty("result").GetProperty("items");
        items.GetArrayLength().ShouldBeGreaterThan(0);
        items[0].GetProperty("section").GetString().ShouldBe("A");

        var history = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees/history");
        history.GetArrayLength().ShouldBe(1);
        history[0].GetProperty("evalId").GetString().ShouldBe(evalId);

        // The processor stamped fee_items rows — Phase 3's override
        // surface keys off their stable ids.
        factory.Services.GetRequiredService<FakeFeeRepository>()
            .Items.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Duplicate_request_returns_same_eval()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithScenarioAsync(officer, base_);

        var first = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/fees", new { });
        var second = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/fees", new { });
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var a = await first.Content.ReadFromJsonAsync<JsonElement>();
        var b = await second.Content.ReadFromJsonAsync<JsonElement>();
        b.GetProperty("evalId").GetString()
            .ShouldBe(a.GetProperty("evalId").GetString());
        b.GetProperty("deduplicated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Borrower_gets_403()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithScenarioAsync(officer, base_);

        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(
                factory, tenant, LolaRole.Borrower));
        var resp = await borrower.PostAsJsonAsync(
            $"{base_}/{appId}/fees", new { });
        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

/// The second IFeeService implementation — a different adapter
/// dropped in behind the same interface proves the seam is the
/// whole contract (MPE-183 swap acceptance).
public sealed class AltStubFeeService : IFeeService
{
    public string Adapter => "alt-fee";

    public Task<FeeEvaluation> EvaluateAsync(
        FeeScenario s, FeeRequestOptions options,
        CancellationToken ct = default)
        => Task.FromResult(new FeeEvaluation(
            $"eval-{Guid.NewGuid():N}", Adapter,
            "2026-10-29T00:00:00.000Z", "alt:v1",
            [
                new FeeLine("origination", "Alt Origination", "A", 750m,
                    [new FeeAdjustment("base", "set", 750m, 750m)],
                    750m),
                new FeeLine("recording", "Alt Recording", "E", 80m,
                    [], 80m)
            ]));
}
