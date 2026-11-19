using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// M15 P3 acceptance (API surface): conditions lifecycle with staff
/// guards + audit/events, the mutation-event list + chain verify,
/// and the borrower-scoped stripped projection.
public sealed class ConditionEndpointTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private async Task<(string Tenant, HttpClient Officer)> StaffAsync()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Cond Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(factory, tenant,
                LolaRole.LoanOfficer));
        return (tenant, officer);
    }

    private static async Task<string> NewAppAsync(HttpClient c, string base_)
        => (await (await c.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

    [Fact]
    public async Task Lifecycle_open_close_waive_reopen_guards()
    {
        var (tenant, officer) = await StaffAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await NewAppAsync(officer, base_);
        var cond = $"{base_}/{appId}/conditions";

        // Open — manual kind, blocking.
        var created = await officer.PostAsJsonAsync(cond,
            new
            {
                kind = "manual",
                title = "Gift letter needed",
                detail = "internal note",
                blocking = true
            });
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var c1 = await created.Content.ReadFromJsonAsync<JsonElement>();
        var cid = c1.GetProperty("conditionId").GetString()!;
        c1.GetProperty("state").GetString().ShouldBe("open");
        c1.GetProperty("blocking").GetBoolean().ShouldBeTrue();

        // List shows it.
        var list = await officer.GetFromJsonAsync<JsonElement>(cond);
        list.GetArrayLength().ShouldBe(1);

        // Waive without reason → 400.
        (await officer.PostAsJsonAsync($"{cond}/{cid}/waive", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Close with reason → closed.
        var closed = await officer.PostAsJsonAsync($"{cond}/{cid}/close",
            new { reason = "doc received" });
        closed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await closed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("closeReason").GetString().ShouldBe("doc received");

        // Reopen → open again.
        var reopened = await officer.PostAsJsonAsync(
            $"{cond}/{cid}/reopen", new { });
        reopened.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Waive with reason → terminal.
        var waived = await officer.PostAsJsonAsync($"{cond}/{cid}/waive",
            new { reason = "not required for this product" });
        waived.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await waived.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("waiverReason").GetString()
            .ShouldBe("not required for this product");

        // Waived is terminal — reopen → 400.
        (await officer.PostAsJsonAsync($"{cond}/{cid}/reopen", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Borrower_view_strips_internal_fields()
    {
        var (tenant, officer) = await StaffAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var user = await factory.SeedUserAsync(tenant,
            $"b-{tenant}@test.dev", PkceLogin.Password, LolaRole.Borrower);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower,
                email: $"b-{tenant}@test.dev", seed: false));

        // Officer opens a condition carrying internal detail on the
        // borrower's own draft.
        var own = await borrower.PostAsJsonAsync(base_, new { });
        var appId = (await own.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        var cond = $"{base_}/{appId}/conditions";
        await officer.PostAsJsonAsync(cond,
            new
            {
                kind = "doc_required",
                title = "Bank statement",
                detail = "internal underwriting note",
                blocking = true
            });

        var view = await borrower.GetFromJsonAsync<JsonElement>(cond);
        view.GetArrayLength().ShouldBe(1);
        var c = view[0];
        c.GetProperty("title").GetString().ShouldBe("Bank statement");
        c.GetProperty("state").GetString().ShouldBe("open");
        // Internal fields are not in the projection at all.
        c.TryGetProperty("detail", out _).ShouldBeFalse();
        c.TryGetProperty("openedBy", out _).ShouldBeFalse();
        c.TryGetProperty("closeReason", out _).ShouldBeFalse();

        // Borrower mutations → 403.
        (await borrower.PostAsJsonAsync(cond,
            new { title = "self-open" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var cid = c.GetProperty("conditionId").GetString()!;
        (await borrower.PostAsJsonAsync($"{cond}/{cid}/close",
            new { reason = "x" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Event list + chain verify → 403 for borrower.
        (await borrower.GetAsync($"{base_}/{appId}/events"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await borrower.GetAsync($"/api/tenants/{tenant}/events/verify"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Event_list_and_chain_verify()
    {
        var (tenant, officer) = await StaffAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await NewAppAsync(officer, base_);
        var cond = $"{base_}/{appId}/conditions";

        // Drive real emission through the api host: open + close a
        // condition → condition_opened + condition_closed rows land in
        // the (fake) tenant event log.
        var created = await officer.PostAsJsonAsync(cond,
            new { title = "paystub" });
        var cid = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("conditionId").GetString()!;
        await officer.PostAsJsonAsync($"{cond}/{cid}/close",
            new { reason = "received" });

        var events = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/events");
        events.GetArrayLength().ShouldBeGreaterThanOrEqualTo(2);
        var kinds = events.EnumerateArray()
            .Select(e => e.GetProperty("eventKind").GetString()).ToList();
        kinds.ShouldContain("condition_opened");
        kinds.ShouldContain("condition_closed");

        var verify = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/events/verify");
        verify.GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Chain_verify_reports_first_broken_seq()
    {
        var (tenant, officer) = await StaffAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await NewAppAsync(officer, base_);
        await officer.PostAsJsonAsync($"{base_}/{appId}/conditions",
            new { title = "paystub" });

        var clean = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/events/verify");
        clean.GetProperty("ok").GetBoolean().ShouldBeTrue();

        // Tamper the stored row — verify must name its seq.
        var log = factory.Services.GetRequiredService<FakeLoanEventLog>();
        var seq = log.Events[0].Seq;
        log.TamperForTest(seq);

        var broken = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/events/verify");
        broken.GetProperty("ok").GetBoolean().ShouldBeFalse();
        broken.GetProperty("firstBrokenSeq").GetInt64().ShouldBe(seq);
        log.UntamperForTest(seq);   // restore — the fake is a singleton
    }

    [Fact]
    public async Task Graphql_conditions_and_events_are_staff_only()
    {
        var (tenant, officer) = await StaffAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await NewAppAsync(officer, base_);
        await officer.PostAsJsonAsync($"{base_}/{appId}/conditions",
            new { title = "paystub" });

        const string q = """
            query($t:String!,$a:String!){ conditions(tenantId:$t,appId:$a)
                { conditionId title state }
                loanEvents(tenantId:$t,appId:$a)
                { eventKind fieldPath } }
            """;
        var staff = await officer.PostAsJsonAsync("/graphql",
            new { query = q, variables = new { t = tenant, a = appId } });
        var staffJson = await staff.Content.ReadFromJsonAsync<JsonElement>();
        staffJson.GetProperty("data").GetProperty("conditions")
            .GetArrayLength().ShouldBe(1);
        staffJson.GetProperty("data").GetProperty("loanEvents")
            .GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);

        await factory.SeedUserAsync(tenant, $"b-{tenant}@test.dev",
            PkceLogin.Password, LolaRole.Borrower);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower,
                email: $"b-{tenant}@test.dev", seed: false));
        var denied = await borrower.PostAsJsonAsync("/graphql",
            new { query = q, variables = new { t = tenant, a = appId } });
        var deniedJson = await denied.Content
            .ReadFromJsonAsync<JsonElement>();
        deniedJson.GetProperty("data").GetProperty("conditions")
            .ValueKind.ShouldBe(JsonValueKind.Null);
        deniedJson.GetProperty("data").GetProperty("loanEvents")
            .ValueKind.ShouldBe(JsonValueKind.Null);
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
}
