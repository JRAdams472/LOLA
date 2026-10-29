using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Audit;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-117 acceptance: audited reveal (officer plaintext + event,
/// borrower 403), staff-only access-review reports exposing classes
/// never values, and control-plane audit on the user-list surface.
public sealed class RevealAndAuditReportTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Officer_reveal_returns_plaintext_and_audit_event()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Reveal Co" }))
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

        // Masked GET is unchanged — the SSN never appears.
        var masked = await (await officer.GetAsync($"{base_}/{appId}"))
            .Content.ReadAsStringAsync();
        masked.ShouldNotContain("123456789");

        // Reveal returns the plaintext doc with a reason.
        var reveal = await officer.PostAsJsonAsync($"{base_}/{appId}/reveal",
            new { reason = "borrower requested verification" });
        reveal.StatusCode.ShouldBe(HttpStatusCode.OK);
        var revealed = await reveal.Content.ReadAsStringAsync();
        revealed.ShouldContain("123456789");

        // The reveal audit event lands in the per-app report —
        // actor/action/classes/reason/utc, never values.
        var audit = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/audit");
        var revealEvent = audit.EnumerateArray()
            .First(e => e.GetProperty("action").GetString() == "reveal");
        revealEvent.GetProperty("fieldClasses").EnumerateArray()
            .Select(c => c.GetString()).ShouldContain("Ssn");
        revealEvent.GetProperty("reason").GetString()
            .ShouldBe("borrower requested verification");
        revealEvent.GetProperty("actor").GetString().ShouldNotBeNullOrEmpty();
        revealEvent.GetProperty("createdUtc").GetString().ShouldNotBeNullOrEmpty();
        audit.GetRawText().ShouldNotContain("123456789");
    }

    [Fact]
    public async Task Borrower_reveal_is_forbidden()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Reveal Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        await factory.SeedUserAsync(tenant,
            $"b-{tenant}@test.dev", PkceLogin.Password, LolaRole.Borrower);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower,
                email: $"b-{tenant}@test.dev", seed: false));
        var base_ = $"/api/tenants/{tenant}/applications";

        var appId = (await (await borrower.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        (await borrower.PostAsJsonAsync($"{base_}/{appId}/reveal",
            new { reason = "curiosity" }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Tenant_audit_report_filters_and_is_staff_only()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Reveal Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));
        var base_ = $"/api/tenants/{tenant}/applications";

        var appId = (await (await officer.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new { patch = new Dictionary<string, string> { ["party[0].ssn"] = "123456789" } });
        await officer.PostAsJsonAsync($"{base_}/{appId}/reveal",
            new { reason = "compliance check" });

        // ?action= filters the tenant-wide report.
        var onlyReveal = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/audit?action=reveal");
        onlyReveal.EnumerateArray().ShouldAllBe(
            e => e.GetProperty("action").GetString() == "reveal");
        onlyReveal.GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
        // ?since= in the future yields nothing — window bounds work.
        var future = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/audit?since=9999-01-01");
        future.GetArrayLength().ShouldBe(0);
        // Unfiltered reports include the write events too.
        var all = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/audit");
        all.GetArrayLength().ShouldBeGreaterThanOrEqualTo(2);

        // Borrowers are refused the report surface entirely.
        await factory.SeedUserAsync(tenant,
            $"b-{tenant}@test.dev", PkceLogin.Password, LolaRole.Borrower);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower,
                email: $"b-{tenant}@test.dev", seed: false));
        (await borrower.GetAsync($"{base_}/{appId}/audit"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await borrower.GetAsync($"/api/tenants/{tenant}/audit"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task User_list_emits_control_plane_audit_event()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Reveal Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));

        (await officer.GetAsync($"/api/admin/tenants/{tenant}/users"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // The control-plane log (real impl on the temp control DB)
        // recorded a `read` on entity user with PII classes only.
        var cpAudit = factory.Services.GetRequiredService<IControlPlaneAuditLog>();
        var events = await cpAudit.QueryAsync(new AuditFilter(Entity: "user"));
        var read = events.FirstOrDefault(e => e.Action == "read");
        read.ShouldNotBeNull();
        read.FieldClasses.ShouldContain("Contact");
        read.FieldClasses.ShouldContain("Name");
        read.FieldClasses.ShouldContain("Account");
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
