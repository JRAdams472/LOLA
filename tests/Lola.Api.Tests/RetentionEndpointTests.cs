using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Audit;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-118 acceptance (API surface): retention report lists per-app
/// windows + earliest-clear, a fresh tenant's erasure is refused with
/// the retention date, and force+reason overrides with a control-plane
/// retention_override audit event that survives the shred.
public sealed class RetentionEndpointTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Report_gate_and_override_flow()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Ret Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));
        var base_ = $"/api/tenants/{tenant}/applications";

        var appId = (await (await officer.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        // Report: one window, earliest-clear = created + 25 months.
        var report = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/retention");
        var win = report.GetProperty("applications").EnumerateArray()
            .Single(a => a.GetProperty("appId").GetString() == appId);
        win.GetProperty("retentionUntilUtc").GetString().ShouldNotBeNullOrEmpty();
        report.GetProperty("earliestClearUtc").GetString()
            .ShouldBe(win.GetProperty("retentionUntilUtc").GetString());

        // Staff-only — a borrower gets 403.
        await factory.SeedUserAsync(tenant,
            $"b-{tenant}@test.dev", PkceLogin.Password, LolaRole.Borrower);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower,
                email: $"b-{tenant}@test.dev", seed: false));
        (await borrower.GetAsync($"/api/tenants/{tenant}/retention"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Inside the window: plain DELETE is refused with the date.
        var refused = await svc.DeleteAsync($"/api/admin/tenants/{tenant}");
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync())
            .ShouldContain("retention window");

        // Force without a reason is 400.
        (await svc.DeleteAsync($"/api/admin/tenants/{tenant}?force=true"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Force + reason proceeds — and the override lands in the
        // control-plane audit log, which outlives the shredded file.
        (await svc.DeleteAsync(
                $"/api/admin/tenants/{tenant}?force=true&reason=court_order"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var cpAudit = factory.Services.GetRequiredService<IControlPlaneAuditLog>();
        var overrides = await cpAudit.QueryAsync(new AuditFilter(
            Entity: "tenant", Action: "retention_override"));
        var ev = overrides.ShouldHaveSingleItem();
        ev.EntityId.ShouldBe(tenant);
        ev.Reason.ShouldBe("court_order");
    }

    private static async Task<string> ServiceTokenAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-svc",
                ["client_secret"] = "svc-secret-value",
                ["scope"] = "lola-api"
            }));
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }
}
