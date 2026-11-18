using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Audit;
using Lola.Application.Identity;
using Lola.Application.Vendors;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-227 acceptance at the API surface: POST …/vendor-orders is
/// 202 + order id, the order drains through VendorOutboxProcessor
/// ordered→submitted→completed with the step events and an
/// encrypted result, reads are staff-only, and duplicate
/// submissions dedupe on request_hash. The e2e runs on
/// AltStubVendorAdapter — the second implementation proving swap is
/// "implement one interface".
public sealed class VendorOrderEndpointTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private static string? _serviceToken;

    private async Task<(string tenant, HttpClient officer)> SetupAsync()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        var made = await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Vendor Co" });
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

    /// A draft carrying the full credit/fraud identity set — the
    /// mapper's egress fields.
    private static async Task<string> DraftWithIdentityAsync(
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
                    ["party[0].first_name"] = "Ada",
                    ["party[0].last_name"] = "Testcase",
                    ["party[0].ssn"] = "123456789",
                    ["party[0].dob"] = "1985-04-12",
                    ["party[0].address[0].street"] = "42 Main St",
                    ["party[0].address[0].city"] = "Springfield",
                    ["party[0].address[0].state"] = "IL",
                    ["party[0].address[0].zip"] = "62701",
                    ["party[0].employment[0].employer_name"] = "Widget Corp",
                    ["party[0].phone"] = "2175550100",
                    ["subject_property.street"] = "1 Elm Ave",
                    ["subject_property.city"] = "Springfield",
                    ["subject_property.state"] = "IL",
                    ["subject_property.zip"] = "62702",
                    ["loan_amount"] = "350000",
                    ["loan_purpose"] = "purchase"
                }
            });
        patch.StatusCode.ShouldBe(HttpStatusCode.OK);
        return appId;
    }

    private async Task<int> DrainAsync(string tenant)
    {
        var registry = factory.Services
            .GetRequiredService<ITenantRegistry>();
        var rec = await registry.FindByIdAsync(tenant);
        return await factory.Services
            .GetRequiredService<Lola.Api.Vendors.VendorOutboxProcessor>()
            .DrainTenantAsync(rec!);
    }

    [Fact]
    public async Task Order_drains_202_to_completed_with_step_events()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithIdentityAsync(officer, base_);

        var placed = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/vendor-orders", new { kind = "credit" });
        placed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await placed.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = body.GetProperty("orderId").GetString()!;
        body.GetProperty("kind").GetString().ShouldBe("credit");
        body.GetProperty("deduplicated").GetBoolean().ShouldBeFalse();

        // Ordered + step event before the drain runs.
        var repo = factory.Services
            .GetRequiredService<FakeVendorOrderRepository>();
        repo.Orders[orderId].Status.ShouldBe("ordered");
        var store = factory.Services
            .GetRequiredService<FakeApplicationStore>();
        store.Steps[appId].ShouldContain(
            e => e.Step == "Credit" && e.Event == "ordered");

        (await DrainAsync(tenant)).ShouldBe(1);

        var order = repo.Orders[orderId];
        order.Status.ShouldBe("completed");
        order.Adapter.ShouldBe("alt-stub");
        order.ExternalId.ShouldStartWith("alt-credit-");
        order.ResultJson.ShouldStartWith("vault:");
        // Completion events land in application_step_events — the
        // fake repo's StepEvents mirror that table.
        repo.StepEvents.ShouldContain(
            e => e.AppId == appId && e.Step == "Credit" &&
                e.Event == "completed");

        // The adapter saw exactly the egress-allowlisted fields —
        // no employer, no property, nothing extra.
        var sent = factory.Services
            .GetRequiredService<AltStubVendorAdapter>()
            .Requests.OfType<CreditOrderRequest>().Last();
        sent.FirstName.ShouldBe("Ada");
        sent.Ssn.ShouldBe("123456789");
        sent.Zip.ShouldBe("62701");
    }

    [Fact]
    public async Task Reads_are_staff_scoped_and_result_is_decrypted()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithIdentityAsync(officer, base_);
        await officer.PostAsJsonAsync(
            $"{base_}/{appId}/vendor-orders", new { kind = "fraud" });
        await DrainAsync(tenant);

        var list = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/vendor-orders");
        list.GetArrayLength().ShouldBe(1);
        var meta = list[0];
        meta.GetProperty("kind").GetString().ShouldBe("fraud");
        meta.GetProperty("status").GetString().ShouldBe("completed");
        // Metadata only — the result never crosses the list read.
        meta.GetProperty("result").ValueKind
            .ShouldBe(JsonValueKind.Null);
        var orderId = meta.GetProperty("orderId").GetString()!;

        var detail = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/{appId}/vendor-orders/{orderId}");
        detail.GetProperty("result").GetProperty("outcome")
            .GetString().ShouldBe("completed");
        detail.GetProperty("result").GetProperty("report")
            .GetProperty("disposition").GetString()
            .ShouldBe("alt-stub-complete");

        // The decrypt read is attributed — field class, not value.
        var audit = factory.Services
            .GetRequiredService<Lola.Application.Audit.ITenantAuditLog>();
        var reads = await audit.QueryAsync(new AuditFilter(
            Action: "vendor_result"));
        reads.ShouldContain(e => e.EntityId == orderId &&
            e.FieldClasses.Contains("VendorReport"));
    }

    [Fact]
    public async Task Duplicate_order_dedupes_on_request_hash()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithIdentityAsync(officer, base_);

        var first = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/vendor-orders", new { kind = "voe" });
        var second = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/vendor-orders", new { kind = "voe" });
        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var a = await first.Content.ReadFromJsonAsync<JsonElement>();
        var b = await second.Content.ReadFromJsonAsync<JsonElement>();
        b.GetProperty("orderId").GetString()
            .ShouldBe(a.GetProperty("orderId").GetString());
        b.GetProperty("deduplicated").GetBoolean().ShouldBeTrue();

        var repo = factory.Services
            .GetRequiredService<FakeVendorOrderRepository>();
        repo.Orders.Values.Count(o => o.ApplicationId == appId)
            .ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_kind_400s_and_missing_fields_400()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";

        // Unknown kind rejects before an order row can exist.
        var draft = await officer.PostAsJsonAsync(base_, new { });
        var appId = (await draft.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        var bad = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/vendor-orders",
            new { kind = "hexapod" });
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // An empty draft lacks every required egress field.
        var missing = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/vendor-orders", new { kind = "credit" });
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await missing.Content.ReadAsStringAsync();
        problem.ShouldContain("first_name");
    }

    [Fact]
    public async Task Request_payload_is_ciphertext_at_rest_and_audited()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithIdentityAsync(officer, base_);

        var placed = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/vendor-orders", new { kind = "credit" });
        var orderId = (await placed.Content
            .ReadFromJsonAsync<JsonElement>())
            .GetProperty("orderId").GetString()!;

        var repo = factory.Services
            .GetRequiredService<FakeVendorOrderRepository>();
        var stored = repo.Orders[orderId].RequestJson;
        stored.ShouldStartWith("vault:");
        stored.ShouldNotContain("123456789"); // ssn never at rest
        stored.ShouldNotContain("Ada");

        var audit = factory.Services
            .GetRequiredService<Lola.Application.Audit.ITenantAuditLog>();
        var placements = await audit.QueryAsync(new AuditFilter(
            Action: "vendor_order"));
        var orderEvent = placements.Single(
            e => e.EntityId == appId);
        // Field classes only — never values.
        orderEvent.FieldClasses.ShouldContain("Ssn");
        orderEvent.FieldClasses.ShouldNotContain("123456789");
        orderEvent.Reason!.ShouldContain("credit");
    }

    [Fact]
    public async Task Borrower_gets_403_on_every_vendor_order_path()
    {
        var (tenant, officer) = await SetupAsync();
        var base_ = $"/api/tenants/{tenant}/applications";
        var appId = await DraftWithIdentityAsync(officer, base_);
        var placed = await officer.PostAsJsonAsync(
            $"{base_}/{appId}/vendor-orders", new { kind = "credit" });
        var orderId = (await placed.Content
            .ReadFromJsonAsync<JsonElement>())
            .GetProperty("orderId").GetString()!;

        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(
                factory, tenant, LolaRole.Borrower));

        (await borrower.PostAsJsonAsync(
            $"{base_}/{appId}/vendor-orders",
            new { kind = "credit" })).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await borrower.GetAsync(
            $"{base_}/{appId}/vendor-orders")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await borrower.GetAsync(
            $"{base_}/{appId}/vendor-orders/{orderId}")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }
}
