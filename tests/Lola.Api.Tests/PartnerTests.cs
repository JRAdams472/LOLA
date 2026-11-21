using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Documents;
using Lola.Application.Identity;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// M19 P1 (MPE-265) marquee — partner invite → accept → brokers group
/// role claim → staff links partner to an application → partner scope
/// is linked-rows-only (403 otherwise) → revoke re-closes the door.
public sealed class PartnerTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Marquee_partner_invite_link_scope_revoke()
    {
        var tenant = $"tpo-{Guid.NewGuid():N}";
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(
                svc, "lola-svc", "svc-secret-value"));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "T" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var adminToken = await PkceLogin.TokenAsync(
            factory, tenant, LolaRole.TenantAdmin);
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization =
            new("Bearer", adminToken);

        // 1. Partner invite — TenantAdmin only, role + org.
        var invite = await admin.PostAsJsonAsync(
            $"/api/admin/tenants/{tenant}/partner-invites",
            new
            {
                email = $"broker-{tenant}@test.dev",
                role = "Broker",
                org = "TPO Originations LLC"
            });
        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
        var link = (await invite.Content
                .ReadFromJsonAsync<JsonElement>())
            .GetProperty("inviteUrl").GetString()!;
        var token = Uri.UnescapeDataString(link.Split("token=")[1]);

        // LoanOfficer cannot mint partner invites (TenantAdmin gate).
        var offToken = await PkceLogin.TokenAsync(
            factory, tenant, LolaRole.LoanOfficer);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", offToken);
        (await officer.PostAsJsonAsync(
                $"/api/admin/tenants/{tenant}/partner-invites",
                new
                {
                    email = "nope@x.dev",
                    role = "Broker",
                    org = "X"
                }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // 2. Accept → user created in brokers group with the org.
        (await factory.CreateClient().PostAsJsonAsync(
                "/api/invites/accept",
                new
                {
                    token,
                    displayName = "Partner One",
                    password = PkceLogin.Password
                }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var brokerToken = await PkceLogin.TokenAsync(
            factory, tenant, LolaRole.Broker, seed: false,
            email: $"broker-{tenant}@test.dev");
        var broker = factory.CreateClient();
        broker.DefaultRequestHeaders.Authorization =
            new("Bearer", brokerToken);
        var info = await broker.GetFromJsonAsync<JsonElement>(
            "/connect/userinfo");
        info.GetProperty("roles")[0].GetString().ShouldBe("Broker");

        // Admin users list carries org_name.
        var users = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/admin/tenants/{tenant}/users");
        var brokerRow = users.EnumerateArray().Single(u =>
            u.GetProperty("email").GetString()
                == $"broker-{tenant}@test.dev");
        brokerRow.GetProperty("orgName").GetString()
            .ShouldBe("TPO Originations LLC");
        var brokerId = brokerRow.GetProperty("id").GetString()!;

        // 3. Officer creates a second application (partner must NOT
        //    reach it) then links the broker to the first.
        var app1 = (await (await officer.PostAsJsonAsync(
                $"/api/tenants/{tenant}/applications", new { }))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        var app2 = (await (await officer.PostAsJsonAsync(
                $"/api/tenants/{tenant}/applications", new { }))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        // Unlinked before link: both read + list are closed/empty.
        (await broker.GetAsync(
                $"/api/tenants/{tenant}/applications/{app1}"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await officer.PutAsJsonAsync(
                $"/api/tenants/{tenant}/applications/{app1}"
                + $"/partners/{brokerId}", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // 4. Partner scope — linked read OK, list is linked-only,
        //    the unlinked app is a hard 403.
        (await broker.GetAsync(
                $"/api/tenants/{tenant}/applications/{app1}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = await broker.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/applications");
        var ids = list.EnumerateArray()
            .Select(a => a.GetProperty("id").GetString()).ToList();
        ids.ShouldBe([app1]);
        (await broker.GetAsync(
                $"/api/tenants/{tenant}/applications/{app2}"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Partner cannot write or mint drafts on linked apps.
        (await broker.PatchAsJsonAsync(
                $"/api/tenants/{tenant}/applications/{app1}",
                new
                {
                    patch = new Dictionary<string, string>
                    { ["loan_amount"] = "5" }
                }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await broker.PostAsJsonAsync(
                $"/api/tenants/{tenant}/applications", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Partner has no staff surface at all.
        (await broker.GetAsync(
                $"/api/admin/tenants/{tenant}/users"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // 5. Audit — partner_linked landed on the app spine.
        var audit = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/applications/{app1}/audit");
        audit.EnumerateArray().Select(a =>
            a.GetProperty("action").GetString())
            .ShouldContain("partner_linked");

        // 6. Revoke — the door closes again.
        (await officer.DeleteAsync(
                $"/api/tenants/{tenant}/applications/{app1}"
                + $"/partners/{brokerId}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await broker.GetAsync(
                $"/api/tenants/{tenant}/applications/{app1}"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// MPE-272 operation matrix — an active application link does NOT
    /// extend a partner's reach to privileged outputs. On the same
    /// LINKED app (with a populated synthetic package): no plaintext
    /// MISMO/ULDD, no package list/detail/download, no signer call —
    /// and the detail read is the narrow metadata projection, never
    /// the staff document.
    [Fact]
    public async Task Linked_partner_denied_privileged_outputs()
    {
        var tenant = $"tpo-{Guid.NewGuid():N}";
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(
                svc, "lola-svc", "svc-secret-value"));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "T" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        // Vendor binding the provisioning step would normally write —
        // required for the completed-package fixture below.
        await factory.Services.GetRequiredService<ITenantRegistry>()
            .SetIspprIdsAsync(tenant, $"cli-{tenant}", $"cust-{tenant}");

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.TenantAdmin));
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));

        // Partner through the real invite → accept → link flow.
        var invite = await admin.PostAsJsonAsync(
            $"/api/admin/tenants/{tenant}/partner-invites",
            new
            {
                email = $"r-{tenant}@t.dev",
                role = "Realtor",
                org = "R"
            });
        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
        var token = Uri.UnescapeDataString(
            (await invite.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("inviteUrl").GetString()!.Split("token=")[1]);
        (await factory.CreateClient().PostAsJsonAsync(
                "/api/invites/accept",
                new
                {
                    token,
                    displayName = "R One",
                    password = PkceLogin.Password
                }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var partner = factory.CreateClient();
        partner.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Realtor,
                seed: false, email: $"r-{tenant}@t.dev"));
        var partnerId = (await admin.GetFromJsonAsync<JsonElement>(
                $"/api/admin/tenants/{tenant}/users"))
            .EnumerateArray().Single(u => u.GetProperty("email").GetString()
                == $"r-{tenant}@t.dev").GetProperty("id").GetString()!;

        var appId = (await (await officer.PostAsJsonAsync(
                $"/api/tenants/{tenant}/applications", new { }))
            .Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        (await officer.PutAsJsonAsync(
                $"/api/tenants/{tenant}/applications/{appId}"
                + $"/partners/{partnerId}", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // A completed package exists on the linked app — denial must
        // not depend on the package being absent.
        var pkg = await officer.PostAsJsonAsync(
            $"/api/tenants/{tenant}/applications/{appId}/packages",
            new
            {
                kind = "adverse-action",
                reasonCodes = new[] { "credit-history" }
            });
        pkg.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var docId = (await pkg.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("docId").GetString()!;
        var rec = await factory.Services
            .GetRequiredService<ITenantRegistry>().FindByIdAsync(tenant);
        (await factory.Services
            .GetRequiredService<Lola.Api.Documents.DocOutboxProcessor>()
            .DrainTenantAsync(rec!)).ShouldBe(1);

        var base_ = $"/api/tenants/{tenant}/applications/{appId}";

        // Linked detail = the narrow projection only.
        var detail = await partner.GetFromJsonAsync<JsonElement>(base_);
        detail.GetProperty("id").GetString().ShouldBe(appId);
        detail.GetProperty("status").GetString().ShouldNotBeNull();
        detail.TryGetProperty("doc", out _).ShouldBeFalse();
        detail.TryGetProperty("borrowerUserId", out _).ShouldBeFalse();
        detail.TryGetProperty("borrowerName", out _).ShouldBeFalse();

        // Privileged outputs on the linked app — all denied, none
        // returning protected content.
        var mismo = await partner.GetAsync($"{base_}/export-mismo");
        mismo.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await mismo.Content.ReadAsStringAsync())
            .TrimStart().ShouldStartWith("{");
        (await partner.GetAsync($"{base_}/export-uldd"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await partner.GetAsync($"{base_}/documents"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await partner.GetAsync($"{base_}/documents/{docId}"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var noRedirect = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            { AllowAutoRedirect = false });
        noRedirect.DefaultRequestHeaders.Authorization =
            partner.DefaultRequestHeaders.Authorization;
        (await noRedirect.GetAsync(
                $"{base_}/documents/{docId}/download"))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // The signer was never reached — no presigned URL was minted.
        ((AltStubDocumentService)factory.Services
            .GetRequiredService<IDocumentService>())
            .DownloadRequests.IsEmpty.ShouldBeTrue();

        // Staff keeps the package surface on the same app.
        (await officer.GetAsync($"{base_}/documents"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.GetAsync($"{base_}/documents/{docId}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static async Task<string> ServiceTokenAsync(
        HttpClient client, string clientId, string secret)
    {
        var resp = await client.PostAsync("/connect/token",
            new FormUrlEncodedContent(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = clientId,
                    ["client_secret"] = secret,
                    ["scope"] = "lola-api"
                }));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }
}
