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
    // lola-svc tokens are tenant-agnostic — mint one per class run.
    // auth-tight caps /connect/token at 10/min per IP; six tests ×
    // svc+officer grants would exceed it (MPE-188).
    private static string? _serviceToken;

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
            .GetProperty("evalIds")[0].GetString()!;

        var pending = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        pending.GetProperty("evals")[0].GetProperty("status").GetString().ShouldBe("pending");
        pending.GetProperty("evals")[0].GetProperty("adapter").GetString().ShouldBe("alt-fee");

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
        done.GetProperty("evals")[0].GetProperty("status").GetString().ShouldBe("done");
        done.GetProperty("evals")[0].GetProperty("adapter").GetString().ShouldBe("alt-fee");
        // Breakdown shape: sections[] with items + effective totals.
        var sections = done.GetProperty("evals")[0].GetProperty("sections");
        sections.GetArrayLength().ShouldBeGreaterThan(0);
        sections[0].GetProperty("section").GetString().ShouldBe("A");
        sections[0].GetProperty("items")[0]
            .GetProperty("feeCode").GetString().ShouldBe("origination");
        done.GetProperty("evals")[0].GetProperty("effectiveTotal").GetDecimal()
            .ShouldBe(done.GetProperty("evals")[0].GetProperty("computedTotal").GetDecimal());

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
        b.GetProperty("evalIds")[0].GetString()
            .ShouldBe(a.GetProperty("evalIds")[0].GetString());
        b.GetProperty("deduplicated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Override_persists_survives_reeval_and_clears()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithScenarioAsync(officer, base_);
        var registry = factory.Services
            .GetRequiredService<ITenantRegistry>();
        var rec = await registry.FindByIdAsync(tenant);
        var processor = factory.Services
            .GetRequiredService<Lola.Api.Fees.FeeOutboxProcessor>();

        // eval 1 → done, two items.
        (await officer.PostAsJsonAsync($"{base_}/{appId}/fees",
            new { })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await processor.DrainTenantAsync(rec!);

        var b1 = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        var item = b1.GetProperty("evals")[0].GetProperty("sections")[0]
            .GetProperty("items")[0];
        var itemId = item.GetProperty("itemId").GetString()!;
        item.GetProperty("feeCode").GetString()
            .ShouldBe("origination");

        // PATCH override — reason required.
        var noReason = await officer.PatchAsJsonAsync(
            $"{base_}/{appId}/fees/{itemId}", new { amount = 600m });
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var set = await officer.PatchAsJsonAsync(
            $"{base_}/{appId}/fees/{itemId}",
            new { amount = 600m, reason = "LO correction" });
        set.StatusCode.ShouldBe(HttpStatusCode.OK);

        var b2 = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        var line = b2.GetProperty("evals")[0].GetProperty("sections")[0]
            .GetProperty("items")[0];
        line.GetProperty("overrideAmount").GetDecimal().ShouldBe(600m);
        line.GetProperty("effective").GetDecimal().ShouldBe(600m);
        b2.GetProperty("evals")[0].GetProperty("effectiveTotal").GetDecimal()
            .ShouldBe(680m);   // 600 + 80 recording

        // Re-eval with a different request → new eval; override
        // survives by feeCode+section.
        (await officer.PostAsJsonAsync($"{base_}/{appId}/fees",
            new { feeCodes = new[] { "origination", "recording" } }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await processor.DrainTenantAsync(rec!);
        var b3 = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        b3.GetProperty("evals")[0].GetProperty("sections")[0].GetProperty("items")[0]
            .GetProperty("effective").GetDecimal().ShouldBe(600m);
        b3.GetProperty("evals")[0].GetProperty("staleOverrides").GetArrayLength().ShouldBe(0);

        // Re-eval without origination → override orphans, not drops.
        (await officer.PostAsJsonAsync($"{base_}/{appId}/fees",
            new { feeCodes = new[] { "recording" } }))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await processor.DrainTenantAsync(rec!);
        var b4 = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        var stale = b4.GetProperty("evals")[0].GetProperty("staleOverrides");
        stale.GetArrayLength().ShouldBe(1);
        stale[0].GetProperty("feeCode").GetString()
            .ShouldBe("origination");
        var overrideId = stale[0].GetProperty("overrideId")
            .GetString()!;

        // Clear via the stale override id — restores, stops affecting.
        var clear = await officer.PatchAsJsonAsync(
            $"{base_}/{appId}/fees/{overrideId}",
            new { amount = (decimal?)null, reason = "rescinded" });
        clear.StatusCode.ShouldBe(HttpStatusCode.OK);
        var b5 = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        b5.GetProperty("evals")[0].GetProperty("staleOverrides").GetArrayLength().ShouldBe(0);
        b5.GetProperty("evals")[0].GetProperty("effectiveTotal").GetDecimal()
            .ShouldBe(b5.GetProperty("evals")[0].GetProperty("computedTotal").GetDecimal());
    }

    [Fact]
    public async Task Variants_run_in_one_batch_and_compare()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithScenarioAsync(officer, base_);
        var rec = await factory.Services
            .GetRequiredService<ITenantRegistry>().FindByIdAsync(tenant);
        var processor = factory.Services
            .GetRequiredService<Lola.Api.Fees.FeeOutboxProcessor>();

        // FHA + conventional in ONE request → one batch, two evals.
        var accepted = await (await officer.PostAsJsonAsync(
            $"{base_}/{appId}/fees", new
            {
                variants = new[]
                {
                    new { loanType = "conventional" },
                    new { loanType = "fha" }
                }
            })).Content.ReadFromJsonAsync<JsonElement>();
        var evalIds = accepted.GetProperty("evalIds");
        evalIds.GetArrayLength().ShouldBe(2);
        accepted.GetProperty("batchId").GetString()
            .ShouldNotBeNullOrEmpty();

        await processor.DrainTenantAsync(rec!);
        var cmp = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        var evals = cmp.GetProperty("evals");
        evals.GetArrayLength().ShouldBe(2);
        var conv = evals.EnumerateArray().Single(e =>
            e.GetProperty("variant").GetString() == "conventional");
        var fha = evals.EnumerateArray().Single(e =>
            e.GetProperty("variant").GetString() == "fha");

        // FHA carries ufmip; conventional doesn't — the variants
        // produced genuinely different fee sets.
        var fhaCodes = fha.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("feeCode").GetString()!))
            .ToList();
        var convCodes = conv.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("feeCode").GetString()!))
            .ToList();
        fhaCodes.ShouldContain("ufmip");
        convCodes.ShouldNotContain("ufmip");

        // Override on the FHA origination item affects FHA only.
        var fhaItem = fha.GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("items").EnumerateArray())
            .Single(i => i.GetProperty("feeCode").GetString()
                == "origination");
        var itemId = fhaItem.GetProperty("itemId").GetString()!;
        (await officer.PatchAsJsonAsync(
            $"{base_}/{appId}/fees/{itemId}",
            new { amount = 600m, reason = "fha correction" }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var cmp2 = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/fees");
        var evals2 = cmp2.GetProperty("evals").EnumerateArray()
            .ToList();
        evals2.Single(e => e.GetProperty("variant").GetString()
                == "fha").GetProperty("sections").EnumerateArray()
            .SelectMany(s => s.GetProperty("items").EnumerateArray())
            .Single(i => i.GetProperty("feeCode").GetString()
                == "origination")
            .GetProperty("effective").GetDecimal().ShouldBe(600m);
        evals2.Single(e => e.GetProperty("variant").GetString()
                == "conventional").GetProperty("sections")
            .EnumerateArray()
            .SelectMany(s => s.GetProperty("items").EnumerateArray())
            .Single(i => i.GetProperty("feeCode").GetString()
                == "origination")
            .GetProperty("overrideAmount").ValueKind
            .ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Duplicate_variant_tags_400()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithScenarioAsync(officer, base_);
        var r = await officer.PostAsJsonAsync($"{base_}/{appId}/fees",
            new
            {
                variants = new[]
                {
                    new { loanType = "fha" },
                    new { loanType = "fha" }
                }
            });
        r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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
    {
        IReadOnlyList<FeeLine> items =
        [
            new FeeLine("origination", "Alt Origination", "A", 750m,
                [new FeeAdjustment("base", "set", 750m, 750m)],
                750m),
            new FeeLine("recording", "Alt Recording", "E", 80m,
                [], 80m)
        ];
        // FHA carries upfront MIP — proves per-variant output.
        if (s.Loan.Type == "fha")
            items = items.Prepend(new FeeLine("ufmip",
                "Upfront MIP", "A", 7437.5m, [], 7437.5m)).ToList();
        if (options.FeeCodes is { } codes)
            items = items.Where(i => codes.Contains(i.FeeCode)).ToList();
        return Task.FromResult(new FeeEvaluation(
            $"eval-{Guid.NewGuid():N}", Adapter,
            "2026-10-29T00:00:00.000Z", "alt:v1", items));
    }
}
