using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-116 acceptance (API surface): PII-touching calls emit audit
/// events, the chain verifies, and verification is staff-only.
public sealed class AuditEndpointTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Pii_calls_emit_events_and_chain_verifies()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Audit Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));
        var base_ = $"/api/tenants/{tenant}/applications";

        var appId = (await (await officer.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        (await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new
            {
                patch = new Dictionary<string, string>
                { ["party[0].ssn"] = "123456789" }
            }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.GetAsync($"{base_}/{appId}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.GetAsync($"{base_}/search?field=ssn&value=123456789"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var verify = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/audit/verify");
        verify.GetProperty("ok").GetBoolean().ShouldBeTrue();
        verify.GetProperty("eventCount").GetInt32().ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Borrower_cannot_verify_audit_chain()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Audit Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await factory.SeedUserAsync(tenant,
            $"b-{tenant}@test.dev", PkceLogin.Password, LolaRole.Borrower);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower,
                email: $"b-{tenant}@test.dev", seed: false));

        (await borrower.GetAsync($"/api/tenants/{tenant}/applications/audit/verify"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
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
