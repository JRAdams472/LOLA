using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lola.Application.Tenancy;
using Lola.Application.Vendors;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-228 webhook acceptance: `POST /api/vendor-webhooks/{kind}` is
/// HMAC-only auth (no OIDC), a valid signature completes the order,
/// invalid → 401, replay → 200 no-op leaving exactly one result and
/// one step event. The test acts as the vendor — the fake cred
/// source supplies the same `webhook_secret` the KV path would.
public sealed class VendorWebhookTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private const string Secret = "test-webhook-secret";
    private static string? _serviceToken;

    private async Task<(string tenant, string appId, string orderId,
        string outboxId, string externalId)> SubmittedOrderAsync()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        var made = await svc.PostAsJsonAsync("/api/admin/tenants",
            new
            {
                tenantId = tenant,
                slug = tenant,
                displayName = "Hook Co"
            });
        made.StatusCode.ShouldBe(HttpStatusCode.Created);

        var repo = factory.Services
            .GetRequiredService<FakeVendorOrderRepository>();
        // Unique ids per test — the shared singleton repo scans all
        // orders by external id, so a hardcoded id would collide.
        var appId = $"app-hook-{Guid.NewGuid():N}"[..20];
        var orderId = $"ord-{Guid.NewGuid():N}";
        var outboxId = $"ob-{Guid.NewGuid():N}";
        var externalId = $"stub-credit-{Guid.NewGuid():N}"[..22];
        using (TenantContext.Use(new TenantScope(
                   tenant, "x.db", "k")))
        {
            var json = VendorRequestMapper.RequestJson(
                new CreditOrderRequest("Ada", "Testcase", "123456789",
                    "1985-04-12", "42 Main St", "Springfield", "IL",
                    "62701"));
            await repo.EnqueueAsync(new VendorOrderRow(
                orderId, appId, "credit", "alt-stub", null,
                "ordered", "vault:req", null, null, "officer-1",
                null, null, null,
                VendorRequestMapper.RequestHash(tenant, appId,
                    VendorKind.Credit, json)), outboxId);
            await repo.MarkSubmittedAsync(orderId,
                externalId, outboxId, null);
        }
        return (tenant, appId, orderId, outboxId, externalId);
    }

    private static StringContent Signed(string tenant,
        string externalId, string? requestHash, out byte[] body)
    {
        var hash = requestHash is null ? "" :
            $"\"request_hash\":\"{requestHash}\",";
        var payload =
            $"{{\"tenant\":\"{tenant}\"," +
            $"\"external_id\":\"{externalId}\",{hash}" +
            "\"outcome\":\"completed\"," +
            "\"report\":{\"reportType\":\"tri-merge\"," +
            "\"external_id\":\"" + externalId + "\"}}";
        body = Encoding.UTF8.GetBytes(payload);
        return new StringContent(payload, Encoding.UTF8,
            "application/json");
    }

    private HttpClient WebhookClient(string tenant, byte[] body,
        string secret = Secret)
    {
        var client = factory.CreateClient();
        var sig = Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body))
            .ToLowerInvariant();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Vendor-Signature", $"sha256={sig}");
        return client;
    }

    private static async Task<string> ServiceTokenAsync(HttpClient c)
    {
        if (_serviceToken is not null) return _serviceToken;
        var resp = await c.PostAsync("/connect/token",
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

    [Fact]
    public async Task Valid_signature_completes_the_order()
    {
        var (tenant, appId, orderId, outboxId, ext) =
            await SubmittedOrderAsync();
        var content = Signed(tenant, ext, null, out var body);
        var resp = await WebhookClient(tenant, body)
            .PostAsync("/api/vendor-webhooks/credit", content);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var repo = factory.Services
            .GetRequiredService<FakeVendorOrderRepository>();
        var order = repo.Orders[orderId];
        order.Status.ShouldBe("completed");
        order.ResultJson.ShouldStartWith("vault:");
        repo.StepEvents.Count(
            e => e.AppId == appId && e.Event == "completed")
            .ShouldBe(1);
        repo.Outbox[outboxId].Status.ShouldBe("done");
    }

    [Fact]
    public async Task Invalid_signature_is_401_and_touches_nothing()
    {
        var (tenant, appId, orderId, _, ext) =
            await SubmittedOrderAsync();
        var content = Signed(tenant, ext, null, out var body);
        var resp = await WebhookClient(tenant, body,
                secret: "wrong-secret")
            .PostAsync("/api/vendor-webhooks/credit", content);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var repo = factory.Services
            .GetRequiredService<FakeVendorOrderRepository>();
        repo.Orders[orderId].Status.ShouldBe("submitted");
        repo.StepEvents.ShouldNotContain(e => e.AppId == appId);
    }

    [Fact]
    public async Task Replay_is_a_200_noop()
    {
        var (tenant, appId, orderId, _, ext) =
            await SubmittedOrderAsync();
        var content = () => Signed(tenant, ext, null, out var b);
        var first = content(); var body = Encoding.UTF8
            .GetBytes(await first.ReadAsStringAsync());

        var client = WebhookClient(tenant, body);
        (await client.PostAsync("/api/vendor-webhooks/credit", first))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync("/api/vendor-webhooks/credit",
                content()))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var repo = factory.Services
            .GetRequiredService<FakeVendorOrderRepository>();
        repo.Orders[orderId].Status.ShouldBe("completed");
        repo.StepEvents.Count(e => e.AppId == appId).ShouldBe(1);
    }

    [Fact]
    public async Task Unknown_external_id_is_404()
    {
        var (tenant, _, _, _, _) = await SubmittedOrderAsync();
        var content = Signed(tenant, "stub-credit-nope",
            null, out var body);
        var resp = await WebhookClient(tenant, body)
            .PostAsync("/api/vendor-webhooks/credit", content);

        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// MPE-230 — a vendor that reuses an external id across orders gets
    /// an explicit 409 (ambiguous), never an arbitrary completion or a
    /// retry-forever 500. The real bug shape: the stub once hashed only
    /// the base request type, so every same-kind order collided.
    [Fact]
    public async Task Duplicate_external_id_is_409_ambiguous()
    {
        var (tenant, appId, _, _, ext) = await SubmittedOrderAsync();
        var repo = factory.Services
            .GetRequiredService<FakeVendorOrderRepository>();
        using (TenantContext.Use(new TenantScope(tenant, "x.db", "k")))
        {
            var json = VendorRequestMapper.RequestJson(
                new CreditOrderRequest("Ada", "Testcase", "123456789",
                    "1985-04-12", "42 Main St", "Springfield", "IL",
                    "62701"));
            var twinId = $"ord-{Guid.NewGuid():N}";
            var twinApp = $"app-hook-twin-{Guid.NewGuid():N}"[..24];
            await repo.EnqueueAsync(new VendorOrderRow(
                twinId, twinApp, "credit", "alt-stub", null,
                "ordered", "vault:req", null, null, "officer-1",
                null, null, null,
                VendorRequestMapper.RequestHash(tenant, twinApp,
                    VendorKind.Credit, json)),
                $"ob-{Guid.NewGuid():N}");
            await repo.MarkSubmittedAsync(twinId, ext,
                repo.OutboxIdFor(twinId)!, null);
        }

        var content = Signed(tenant, ext, null, out var body);
        var resp = await WebhookClient(tenant, body)
            .PostAsync("/api/vendor-webhooks/credit", content);

        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Missing_signature_is_401()
    {
        var (tenant, _, _, _, ext) = await SubmittedOrderAsync();
        var content = Signed(tenant, ext, null, out _);
        var resp = await factory.CreateClient()
            .PostAsync("/api/vendor-webhooks/credit", content);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
