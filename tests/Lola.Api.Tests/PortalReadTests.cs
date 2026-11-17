using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-221 portal read surface over the real wire: the portalLoan(s)
/// resolvers return the borrower-safe projection — masked PII, friendly
/// status vocabulary, and a serialized payload that provably contains
/// none of the officer-only/internal fields.
public class PortalReadTests(LolaApiFactory factory) : IClassFixture<LolaApiFactory>
{
    private async Task<(HttpClient Client, string AppId)> SeedBorrowerWithDraft(
        string tenant, string userTag)
    {
        var token = await PkceLogin.TokenAsync(factory, tenant,
            LolaRole.Borrower, email: $"{userTag}-{tenant}@test.dev");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

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
                }
            });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (client, appId);
    }

    [Fact]
    public async Task PortalLoan_returns_masked_borrower_safe_payload()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var (client, appId) = await SeedBorrowerWithDraft(tenant, "b1");

        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                { portalLoan(tenantId: "{{tenant}}", appId: "{{appId}}") {
                    id statusLabel statusStage
                    statusStages { key label done current }
                    docJson
                    uploads { vaultDocId classification statusLabel
                              statusKey filename sizeBytes uploadedUtc }
                    documents { docId packageName kindLabel ready
                                filename completedUtc } } }
                """
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await resp.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(raw).RootElement;
        var loan = body.GetProperty("data").GetProperty("portalLoan");
        loan.GetProperty("id").GetString().ShouldBe(appId);
        loan.GetProperty("statusLabel").GetString()
            .ShouldBe("Application in progress");
        loan.GetProperty("statusStages").EnumerateArray()
            .Select(s => s.GetProperty("key").GetString())
            .ShouldBe(["applying", "review", "decision"]);
        loan.GetProperty("statusStages")[0]
            .GetProperty("current").GetBoolean().ShouldBeTrue();

        // Masked-confirm on the real wire — borrower scope masks all PII.
        var doc = JsonDocument.Parse(
            loan.GetProperty("docJson").GetString()!).RootElement;
        var ssn = doc.GetProperty("party")[0]
            .GetProperty("ssn").GetString()!;
        ssn.ShouldStartWith("•••");
        ssn.ShouldNotContain("123456");

        // The contract, on the serialized bytes: no internal/officer fields.
        foreach (var forbidden in new[]
        {
            "pipelineStep", "currentVersion", "borrowerUserId",
            "trid", "POSIntake", "\"status\"",
        })
            raw.ShouldNotContain(forbidden);
    }

    [Fact]
    public async Task PortalLoans_lists_only_own_apps_in_borrower_vocabulary()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var (client, appId) = await SeedBorrowerWithDraft(tenant, "b1");
        await SeedBorrowerWithDraft(tenant, "b2");   // another borrower's app

        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""{ portalLoans(tenantId: "{{tenant}}") { id statusLabel } }"""
        });
        var loans = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("portalLoans").EnumerateArray()
            .ToList();
        loans.Count.ShouldBe(1);
        loans[0].GetProperty("id").GetString().ShouldBe(appId);
        loans[0].GetProperty("statusLabel").GetString()
            .ShouldBe("Application in progress");
    }

    [Fact]
    public async Task PortalLoan_denies_other_borrowers_app()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var (_, appId) = await SeedBorrowerWithDraft(tenant, "b1");
        var (other, _) = await SeedBorrowerWithDraft(tenant, "b2");

        var resp = await other.PostAsJsonAsync("/graphql", new
        {
            query = $$"""{ portalLoan(tenantId: "{{tenant}}", appId: "{{appId}}") { id } }"""
        });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("portalLoan")
            .ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("errors").GetArrayLength()
            .ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Officer_gets_null_on_portal_resolvers()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));

        var resp = await officer.PostAsJsonAsync("/graphql", new
        {
            query = $$"""{ portalLoans(tenantId: "{{tenant}}") { id } }"""
        });
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").GetProperty("portalLoans")
            .GetArrayLength().ShouldBe(0);
    }

    /// The officer read surface must be empty to a borrower token —
    /// portalLoan(s) is the only application read on this plane. The
    /// internal fields below are what would otherwise leak:
    /// pipelineStep / trid* / pricing internals / AV errors.
    [Fact]
    public async Task Borrower_denied_every_officer_read_resolver()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var (client, appId) = await SeedBorrowerWithDraft(tenant, "b1");

        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                { application(tenantId: "{{tenant}}", appId: "{{appId}}") { pipelineStep }
                  applications(tenantId: "{{tenant}}") { pipelineStep }
                  pricing(tenantId: "{{tenant}}", appId: "{{appId}}") { status }
                  rateLock(tenantId: "{{tenant}}", appId: "{{appId}}") { lockedUtc }
                  fees(tenantId: "{{tenant}}", appId: "{{appId}}") { evals { status } }
                  documents(tenantId: "{{tenant}}", appId: "{{appId}}") { status }
                  uploads(tenantId: "{{tenant}}", appId: "{{appId}}") { status sha256 }
                  steps(tenantId: "{{tenant}}", appId: "{{appId}}") { step } }
                """
        });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var data = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data");
        data.GetProperty("application").ValueKind.ShouldBe(JsonValueKind.Null);
        data.GetProperty("pricing").ValueKind.ShouldBe(JsonValueKind.Null);
        data.GetProperty("rateLock").ValueKind.ShouldBe(JsonValueKind.Null);
        data.GetProperty("fees").ValueKind.ShouldBe(JsonValueKind.Null);
        foreach (var list in new[]
        {
            "applications", "documents", "uploads", "steps",
        })
            data.GetProperty(list).GetArrayLength().ShouldBe(0);
    }

    /// MPE-224 hardening gate: the GraphQL officer read surface was
    /// locked in MPE-221, but the same officer-eval reads were still
    /// reachable over REST by a borrower token. These now 403, while
    /// the own-app reads the borrower surface legitimately needs
    /// (own doc, own list, documents, uploads) stay green.
    [Fact]
    public async Task Borrower_denied_officer_eval_reads_over_rest()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var (client, appId) = await SeedBorrowerWithDraft(tenant, "b1");
        var base_ = $"/api/tenants/{tenant}/applications";

        foreach (var path in new[]
        {
            $"{appId}/pricing", $"{appId}/pricing/history",
            $"{appId}/lock", $"{appId}/fees", $"{appId}/fees/history",
            $"{appId}/steps", $"{appId}/export-uldd",
        })
            (await client.GetAsync($"{base_}/{path}"))
                .StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);

        // Own-app reads the borrower surface keeps by design.
        (await client.GetAsync($"{base_}/{appId}"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync(base_))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"{base_}/{appId}/documents"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync($"{base_}/{appId}/uploads"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
