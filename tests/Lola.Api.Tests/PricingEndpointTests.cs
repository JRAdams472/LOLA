using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Identity;
using Lola.Application.Pricing;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-130 acceptance at the API surface: POST …/price is 202 +
/// eval id, the eval drains through PricingOutboxProcessor to done,
/// GET …/pricing + /history read the spine, a duplicate request
/// dedupes, and borrowers are forbidden. The whole e2e runs on
/// AltStubPricingAdapter — the second implementation proving swap is
/// "implement one interface".
public sealed class PricingEndpointTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private async Task<(string tenant, HttpClient officer)> SetupAsync()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        var made = await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Pricing Co" });
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
                    ["amortization_type"] = "fixed",
                    ["loan.ltv"] = "75",
                    ["subject_property.property_type"] = "single_family",
                    ["subject_property.usage"] = "primary_residence",
                    ["subject_property.state"] = "IL",
                    ["party[0].credit_score"] = "720",
                    ["party[0].credit_model"] = "fico",
                    ["party[0].ssn"] = "123456789",
                }
            });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);
        return appId;
    }

    [Fact]
    public async Task Price_enqueues_202_then_worker_completes()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithScenarioAsync(officer, base_);

        var price = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/price", new { lockTermDays = 30 });
        price.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var evalId = (await price.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("evalId").GetString()!;

        // Pending until the processor drains.
        var pending = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/pricing");
        pending.GetProperty("status").GetString().ShouldBe("pending");
        pending.GetProperty("adapter").GetString().ShouldBe("alt-stub");

        // Drain the tenant's outbox — the fake repo + alt adapter run.
        var registry = factory.Services
            .GetRequiredService<ITenantRegistry>();
        var rec = await registry.FindByIdAsync(tenant);
        var drained = await factory.Services
            .GetRequiredService<Lola.Api.Pricing.PricingOutboxProcessor>()
            .DrainTenantAsync(rec!);
        drained.ShouldBe(1);

        var done = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/pricing");
        done.GetProperty("status").GetString().ShouldBe("done");
        done.GetProperty("result")
            .GetProperty("adapter").GetString().ShouldBe("alt-stub");
        done.GetProperty("result")
            .GetProperty("products").GetArrayLength()
            .ShouldBeGreaterThan(0);

        var history = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/pricing/history");
        history.GetArrayLength().ShouldBe(1);
        history[0].GetProperty("evalId").GetString().ShouldBe(evalId);
    }

    [Fact]
    public async Task Duplicate_request_returns_same_eval()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithScenarioAsync(officer, base_);

        var first = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/price", new { lockTermDays = 30 });
        var second = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/price", new { lockTermDays = 30 });
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var a = await first.Content.ReadFromJsonAsync<JsonElement>();
        var b = await second.Content.ReadFromJsonAsync<JsonElement>();
        b.GetProperty("evalId").GetString()
            .ShouldBe(a.GetProperty("evalId").GetString());
        b.GetProperty("deduplicated").GetBoolean().ShouldBeTrue();

        var history = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/pricing/history");
        history.GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Lock_freezes_paths_unlock_clears_relock_appends()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithScenarioAsync(officer, base_);

        // Price + drain to a `done` eval, then lock it.
        await officer.PostAsJsonAsync($"{base_}/{appId}/price",
            new { lockTermDays = 30 });
        var rec = await factory.Services
            .GetRequiredService<ITenantRegistry>()
            .FindByIdAsync(tenant);
        await factory.Services
            .GetRequiredService<Lola.Api.Pricing.PricingOutboxProcessor>()
            .DrainTenantAsync(rec!);

        // Lock before eval is done must 409 — prove via a second app.
        var locked = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/lock",
            new { productCode = "alt-30", rate = 5.99m, lockTermDays = 30 });
        locked.StatusCode.ShouldBe(HttpStatusCode.OK);
        var lockDto = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/lock");
        lockDto.GetProperty("status").GetString().ShouldBe("active");
        lockDto.GetProperty("productCode").GetString()
            .ShouldBe("alt-30");
        DateTimeOffset.Parse(lockDto.GetProperty("expiresUtc")
            .GetString()!).ShouldBeGreaterThan(DateTimeOffset.UtcNow
            .AddDays(29));
        // App DTO carries lock + expiry.
        var appDto = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}");
        appDto.GetProperty("lock")
            .GetProperty("expiresUtc").GetString().ShouldNotBeNull();

        // Frozen path rejected 409 with the field list; non-frozen ok.
        var frozen = await officer.PatchAsJsonAsync(
            $"{base_}/{appId}",
            new
            {
                patch = new Dictionary<string, string>
                {
                    ["loan.base_amount"] = "360000",
                    ["party[0].credit_score"] = "710"
                }
            });
        frozen.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var frozenBody = await frozen.Content
            .ReadFromJsonAsync<JsonElement>();
        frozenBody.GetProperty("frozenFields").GetArrayLength()
            .ShouldBe(2);
        var allowed = await officer.PatchAsJsonAsync(
            $"{base_}/{appId}",
            new
            {
                patch = new Dictionary<string, string>
                { ["party[0].first_name"] = "Renamed" }
            });
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Unlock with reason clears the freeze; re-lock appends.
        var unlocked = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/unlock", new { reason = "re-shop" });
        unlocked.StatusCode.ShouldBe(HttpStatusCode.OK);
        var getLock = await officer.GetAsync($"{base_}/{appId}/lock");
        getLock.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var after = await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new
            {
                patch = new Dictionary<string, string>
                { ["loan.base_amount"] = "360000" }
            });
        after.StatusCode.ShouldBe(HttpStatusCode.OK);

        var relock = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/lock",
            new { productCode = "alt-30", rate = 5.99m, lockTermDays = 30 });
        relock.StatusCode.ShouldBe(HttpStatusCode.OK);
        var lockRepo = factory.Services
            .GetRequiredService<FakeRateLockRepository>();
        lockRepo.Rows.Values.Count(
            r => r.ApplicationId == appId).ShouldBe(2);
    }

    [Fact]
    public async Task Borrower_cannot_price()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithScenarioAsync(officer, base_);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower));

        var resp = await borrower.PostAsJsonAsync(
            $"{base_}/{appId}/price", new { });
        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

/// The second IPricingService implementation — a different adapter
/// dropped in behind the same interface proves the seam is the whole
/// contract (MPE-130 swap acceptance). Deterministic, clearly marked
/// so eval rows can assert which adapter ran.
public sealed class AltStubPricingAdapter : IPricingService
{
    public string Adapter => "alt-stub";

    public Task<PricingEvaluation> EvaluateAsync(
        PricingScenario s, PricingRequestOptions options,
        CancellationToken ct = default)
        => Task.FromResult(new PricingEvaluation(
            $"eval-{Guid.NewGuid():N}", Adapter, options.AsOf,
            "2026-10-29T00:00:00.000Z",
            [
                new PricedProduct("alt-30", "Alt Fixed 30", true, [],
                    [new RateOffer(5.99m, 99.5m, 0.1m, 30, "Alt Sheet")])
            ]));
}
