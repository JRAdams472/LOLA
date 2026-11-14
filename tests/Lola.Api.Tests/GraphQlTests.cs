using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Lola.Api.Tests;

public class GraphQlTests(LolaApiFactory factory) : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Ping_routes_through_mediator()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync("/graphql",
            new { query = "{ ping }" });
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("\"ping\":\"pong\"");
    }

    /// MPE-209 masking parity (ADR-0011): the pipeline read-model must
    /// return the same borrowerName/mask shape on GraphQL as REST —
    /// same handler, same VisibilityContext.
    [Theory]
    [InlineData(LolaRole.LoanOfficer, "Ada Lovelace")]
    [InlineData(LolaRole.Service, "•••lace")]
    public async Task Applications_list_masks_identically_to_rest(
        LolaRole role, string expectedName)
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        // Seed the name-bearing draft as an officer; read it as `role`.
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));
        var appId = await SeedNamedDraftAsync(officer, tenant);

        var token = role == LolaRole.LoanOfficer
            ? officer.DefaultRequestHeaders.Authorization!.Parameter!
            : await PkceLogin.TokenAsync(factory, tenant, role);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var rest = await client.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/applications");
        var gql = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""{ applications(tenantId: "{{tenant}}") { id status borrowerName tridDeadlineUtc } }"""
        });
        gql.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await gql.Content.ReadFromJsonAsync<JsonElement>();
        var gqlApps = body.GetProperty("data").GetProperty("applications");

        var restRow = rest.EnumerateArray().Single(a =>
            a.GetProperty("id").GetString() == appId);
        var gqlRow = gqlApps.EnumerateArray().Single(a =>
            a.GetProperty("id").GetString() == appId);

        gqlRow.GetProperty("borrowerName").GetString().ShouldBe(expectedName);
        restRow.GetProperty("borrowerName").GetString()
            .ShouldBe(gqlRow.GetProperty("borrowerName").GetString());
        // TRID stamped by the six-field patch — deadline non-null on both.
        gqlRow.GetProperty("tridDeadlineUtc").GetString()
            .ShouldBe(restRow.GetProperty("tridDeadlineUtc").GetString());
        gqlRow.GetProperty("tridDeadlineUtc").GetString().ShouldNotBeNull();
    }

    /// Blind search returns the same application ids on both surfaces.
    [Fact]
    public async Task Search_returns_same_ids_as_rest()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));
        var appId = await SeedNamedDraftAsync(officer, tenant);

        var rest = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/applications/search?field=ssn&value=123456789");
        var gql = await officer.PostAsJsonAsync("/graphql", new
        {
            query = $$"""{ search(tenantId: "{{tenant}}", field: "ssn", value: "123456789") { applicationIds } }"""
        });
        gql.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await gql.Content.ReadFromJsonAsync<JsonElement>();
        var gqlIds = body.GetProperty("data").GetProperty("search")
            .GetProperty("applicationIds")
            .EnumerateArray().Select(i => i.GetString()).ToList();
        var restIds = rest.GetProperty("applicationIds")
            .EnumerateArray().Select(i => i.GetString()).ToList();

        gqlIds.ShouldBe(restIds);
        gqlIds.ShouldContain(appId);
    }

    /// Steps resolver over the same handler — the draft-creation +
    /// patch events surface identically.
    [Fact]
    public async Task Steps_and_application_detail_resolve()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));
        var appId = await SeedNamedDraftAsync(officer, tenant);

        var gql = await officer.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                {
                  steps(tenantId: "{{tenant}}", appId: "{{appId}}") { step event actor }
                  application(tenantId: "{{tenant}}", appId: "{{appId}}") { id status tridDeadlineUtc }
                }
                """
        });
        gql.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await gql.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("application")
            .GetProperty("id").GetString().ShouldBe(appId);
        body.GetProperty("data").GetProperty("application")
            .GetProperty("tridDeadlineUtc").GetString().ShouldNotBeNull();
    }

    /// MPE-211: the admin membership read resolves through the same
    /// handler as REST — group + member created over REST, member list
    /// read over GraphQL.
    [Fact]
    public async Task Group_members_resolve_via_graphql()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.TenantAdmin));

        var group = await admin.PostAsJsonAsync(
            $"/api/admin/tenants/{tenant}/groups",
            new { name = "officers", roles = new[] { "LoanOfficer" } });
        group.StatusCode.ShouldBe(HttpStatusCode.Created);
        var gid = (await group.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var me = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/admin/tenants/{tenant}/users");
        var uid = me.EnumerateArray().First()
            .GetProperty("id").GetString()!;
        (await admin.PostAsJsonAsync(
            $"/api/admin/tenants/{tenant}/groups/{gid}/members",
            new { userId = uid })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var gql = await admin.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                { groupMembers(tenantId: "{{tenant}}", groupId: "{{gid}}") }
                """
        });
        gql.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await gql.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("groupMembers")
            .EnumerateArray().Select(m => m.GetString()).ShouldBe([uid]);
    }

    private static async Task<string> SeedNamedDraftAsync(HttpClient client, string tenant)
    {
        var created = await client.PostAsJsonAsync(
            $"/api/tenants/{tenant}/applications", new { });
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var appId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;

        var patch = await client.PatchAsJsonAsync(
            $"/api/tenants/{tenant}/applications/{appId}", new
            {
                patch = new Dictionary<string, string>
                {
                    ["party[0].role"] = "borrower",
                    ["party[0].first_name"] = "Ada",
                    ["party[0].last_name"] = "Lovelace",
                    ["party[0].ssn"] = "123456789",
                    ["party[0].employment[0].monthly_income"] = "14200",
                    ["subject_property.street"] = "1 Prime Plaza",
                    ["subject_property.est_value"] = "560000",
                    ["loan_amount"] = "425000",
                }
            });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);
        return appId;
    }
}
