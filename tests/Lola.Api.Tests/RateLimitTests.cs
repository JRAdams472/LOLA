using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-114 acceptance: the intake surface is behind the `intake`
/// rate-limit policy — a per-tenant+actor budget that rejects with
/// 429 once exhausted, without affecting other tenants.
public sealed class RateLimitTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Intake_endpoints_reject_after_budget_exhausted()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Limit Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));

        var base_ = $"/api/tenants/{tenant}/applications";
        var saw429 = false;
        for (var i = 0; i < 130 && !saw429; i++)
            saw429 = (await officer.PostAsJsonAsync(base_, new { }))
                .StatusCode == HttpStatusCode.TooManyRequests;
        saw429.ShouldBeTrue("the intake budget (120/min per tenant+actor) must reject");

        // A different tenant is unaffected — partition is tenant+actor.
        var other = $"t{Guid.NewGuid():N}"[..12];
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = other, slug = other, displayName = "Other Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer2 = factory.CreateClient();
        officer2.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(factory, other, LolaRole.LoanOfficer));
        (await officer2.PostAsJsonAsync($"/api/tenants/{other}/applications", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
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
