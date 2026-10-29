using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-111 — tenant-scoped MISMO/ULDD interop endpoints over the real
/// HTTP surface (repos faked per the API-test fixture convention).
public sealed class InteropEndpointTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private async Task<(string tenant, HttpClient officer, string appId)>
        ProvisionDraftAsync()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        var tok = await svc.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-svc",
                ["client_secret"] = "svc-secret-value"
            }));
        svc.DefaultRequestHeaders.Authorization = new("Bearer",
            (await tok.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("access_token").GetString()!);
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Interop Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));
        var created = await officer.PostAsJsonAsync(
            $"/api/tenants/{tenant}/applications", new { });
        var appId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        (await officer.PatchAsJsonAsync(
            $"/api/tenants/{tenant}/applications/{appId}",
            new
            {
                patch = new Dictionary<string, string>
                {
                    ["loan_amount"] = "425000",
                    ["party[0].first_name"] = "Ada",
                    ["party[0].ssn"] = "123456789",
                }
            })).StatusCode.ShouldBe(HttpStatusCode.OK);
        return (tenant, officer, appId);
    }

    [Fact]
    public async Task Export_mismo_then_import_creates_equal_draft()
    {
        var (tenant, officer, appId) = await ProvisionDraftAsync();
        var base_ = $"/api/tenants/{tenant}/applications";

        // export — real values (document interchange), XML content type
        var exported = await officer.GetAsync($"{base_}/{appId}/export-mismo");
        exported.StatusCode.ShouldBe(HttpStatusCode.OK);
        exported.Content.Headers.ContentType!.MediaType
            .ShouldBe("application/xml");
        var xml = await exported.Content.ReadAsStringAsync();
        xml.ShouldContain("<FirstName>Ada</FirstName>");
        xml.ShouldContain("LoanAmount>425000<");

        // import → NEW draft, fields land on catalog paths
        var imported = await officer.PostAsJsonAsync($"{base_}/import-mismo",
            new { xml });
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await imported.Content.ReadFromJsonAsync<JsonElement>();
        var newId = body.GetProperty("id").GetString()!;
        newId.ShouldNotBe(appId);
        body.GetProperty("importedFields").GetInt32().ShouldBeGreaterThan(2);

        var got = await officer.GetAsync($"{base_}/{newId}");
        var doc = (await got.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("doc");
        doc.GetProperty("loan_amount").GetString().ShouldBe("425000");
        doc.GetProperty("party")[0].GetProperty("first_name")
            .GetString().ShouldBe("Ada");
    }

    [Fact]
    public async Task Export_uldd_is_wellformed_with_containers()
    {
        var (tenant, officer, appId) = await ProvisionDraftAsync();
        var resp = await officer.GetAsync(
            $"/api/tenants/{tenant}/applications/{appId}/export-uldd");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var xml = await resp.Content.ReadAsStringAsync();
        xml.ShouldContain("<ULDD>");
        xml.ShouldContain("<LOAN>");
        xml.ShouldContain("NoteAmount>425000<");
    }

    [Fact]
    public async Task Borrower_export_scoped_to_own_draft()
    {
        var (tenant, officer, appId) = await ProvisionDraftAsync();
        var user = await factory.SeedUserAsync(tenant,
            $"b-{tenant}@test.dev", PkceLogin.Password, LolaRole.Borrower);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower,
                email: $"b-{tenant}@test.dev", seed: false));

        // someone else's application → 403
        (await borrower.GetAsync(
            $"/api/tenants/{tenant}/applications/{appId}/export-mismo"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // own import → attributed to the borrower
        var xml = await (await officer.GetAsync(
            $"/api/tenants/{tenant}/applications/{appId}/export-mismo"))
            .Content.ReadAsStringAsync();
        var imported = await borrower.PostAsJsonAsync(
            $"/api/tenants/{tenant}/applications/import-mismo", new { xml });
        imported.StatusCode.ShouldBe(HttpStatusCode.OK);
        var newId = (await imported.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        var own = await borrower.GetAsync(
            $"/api/tenants/{tenant}/applications/{newId}");
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await own.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("borrowerUserId").GetString().ShouldBe(user.Id);
    }
}
