using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lola.Application.Events;
using Lola.Application.Tasks;
using Lola.Application.Tenancy;
using Lola.Application.Vendors;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-259 ingress acceptance: `POST /api/vendor-webhooks/{source}/
/// validation` — HMAC-only auth like order callbacks, bounded body,
/// source whitelist. A signed receipt reaches the P2 handler
/// end-to-end (condition + task land); forged/unsigned → 401;
/// replay → 200 duplicate with no second write.
public sealed class ValidationWebhookTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    private const string Secret = "test-webhook-secret";
    private static string? _serviceToken;

    private async Task<(string Tenant, string AppId)> SetupAsync()
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
        var appId = $"app-vf-{Guid.NewGuid():N}"[..18];
        factory.Services.GetRequiredService<FakeApplicationStore>()
            .Apps[appId] = new FakeApplicationStore.App
            { Status = "submitted" };
        return (tenant, appId);
    }

    private static StringContent Signed(string tenant, string appId,
        string feedbackId, string finding, string severity,
        string? conditionId, out byte[] body)
    {
        var cond = conditionId is null ? "" :
            $"\"condition_id\":\"{conditionId}\",";
        var payload =
            $"{{\"tenant\":\"{tenant}\"," +
            $"\"application_id\":\"{appId}\",{cond}" +
            $"\"feedback_id\":\"{feedbackId}\"," +
            $"\"finding\":\"{finding}\"," +
            $"\"severity\":\"{severity}\"}}";
        body = Encoding.UTF8.GetBytes(payload);
        return new StringContent(payload, Encoding.UTF8,
            "application/json");
    }

    private HttpClient WebhookClient(byte[] body,
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
    public async Task Signed_mpex_clear_auto_closes_the_condition()
    {
        var (tenant, appId) = await SetupAsync();
        var conds = factory.Services
            .GetRequiredService<IConditionRepository>();
        using (TenantContext.Use(new TenantScope(tenant, "x.db", "k")))
            await conds.OpenAsync(new ConditionDraft(appId,
                ConditionKind.DataValidation,
                "mpex validation — data_error", Blocking: true,
                LinkedEntity: FeedbackLink.VendorFeedback,
                LinkedEntityId: "vf-0", OpenedBy: "auto:mpex"));
        var cond = (await conds.ListByApplicationAsync(appId))
            .Single();

        var content = Signed(tenant, appId, $"vf-{Guid.NewGuid():N}"[..12], "cleared", "info",
            cond.ConditionId, out var body);
        var resp = await WebhookClient(body).PostAsync(
            "/api/vendor-webhooks/mpex/validation", content);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString().ShouldBe("recorded");
        var after = await conds.GetAsync(cond.ConditionId);
        after!.State.ShouldBe(ConditionState.Closed);
        // Both spines: validation_received + condition_closed.
        var events = factory.Services
            .GetRequiredService<FakeLoanEventLog>();
        events.Events.Select(d => d.EventKind).ShouldContain(
            LoanEventKind.ValidationReceived);
        events.Events.Select(d => d.EventKind).ShouldContain(
            LoanEventKind.ConditionClosed);
        // The derived task resolved with the condition.
        var tasks = await factory.Services
            .GetRequiredService<ITaskRepository>()
            .ListQueueAsync(tenant);
        tasks.ShouldBeEmpty();
    }

    [Fact]
    public async Task Signed_isppr_exception_opens_condition_and_task()
    {
        var (tenant, appId) = await SetupAsync();
        var content = Signed(tenant, appId, $"vf-{Guid.NewGuid():N}"[..12], "exception",
            "error", null, out var body);
        var resp = await WebhookClient(body).PostAsync(
            "/api/vendor-webhooks/isppr/validation", content);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var conds = await factory.Services
            .GetRequiredService<IConditionRepository>()
            .ListByApplicationAsync(appId);
        var cond = conds.ShouldHaveSingleItem();
        cond.Kind.ShouldBe(ConditionKind.DataValidation);
        cond.Blocking.ShouldBeTrue();
        cond.OpenedBy.ShouldBe("auto:isppr");

        var tasks = await factory.Services
            .GetRequiredService<ITaskRepository>()
            .ListQueueAsync(tenant);
        var task = tasks.ShouldHaveSingleItem();
        task.Kind.ShouldBe(TaskKind.ValidationException);
        task.BlockingEntityId.ShouldBe(cond.ConditionId);
    }

    [Fact]
    public async Task Forged_signature_is_401_and_touches_nothing()
    {
        var (tenant, appId) = await SetupAsync();
        var content = Signed(tenant, appId, $"vf-{Guid.NewGuid():N}"[..12], "exception",
            "error", null, out var body);
        var resp = await WebhookClient(body, secret: "forged-secret")
            .PostAsync("/api/vendor-webhooks/mpex/validation",
                content);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var ledger = factory.Services
            .GetRequiredService<FakeVendorFeedbackRepository>();
        ledger.Rows.ShouldNotContain(
            r => r.ApplicationId == appId);
        (await factory.Services
                .GetRequiredService<IConditionRepository>()
                .ListByApplicationAsync(appId))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_signature_is_401()
    {
        var (tenant, appId) = await SetupAsync();
        var content = Signed(tenant, appId, $"vf-{Guid.NewGuid():N}"[..12], "cleared",
            "info", null, out _);
        var resp = await factory.CreateClient().PostAsync(
            "/api/vendor-webhooks/mpex/validation", content);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Replay_is_a_200_duplicate_with_no_second_write()
    {
        var (tenant, appId) = await SetupAsync();
        var fid = $"vf-{Guid.NewGuid():N}"[..12];
        var body = Encoding.UTF8.GetBytes(
            $"{{\"tenant\":\"{tenant}\"," +
            $"\"application_id\":\"{appId}\"," +
            $"\"feedback_id\":\"{fid}\",\"finding\":\"data_error\"," +
            "\"severity\":\"warning\"}");
        var client = WebhookClient(body);
        var content = () => new StringContent(
            Encoding.UTF8.GetString(body), Encoding.UTF8,
            "application/json");

        (await client.PostAsync(
                "/api/vendor-webhooks/mpex/validation", content()))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = await client.PostAsync(
            "/api/vendor-webhooks/mpex/validation", content());

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString().ShouldBe("duplicate");
        var ledger = factory.Services
            .GetRequiredService<FakeVendorFeedbackRepository>();
        ledger.Rows.Count(r => r.FeedbackId == fid).ShouldBe(1);
        (await factory.Services
                .GetRequiredService<IConditionRepository>()
                .ListByApplicationAsync(appId))
            .ShouldHaveSingleItem();   // one condition, not two
    }

    [Fact]
    public async Task Unknown_source_is_404()
    {
        var content = new StringContent(
            "{\"tenant\":\"x\",\"application_id\":\"a\"," +
            "\"feedback_id\":\"f\",\"finding\":\"cleared\"," +
            "\"severity\":\"info\"}", Encoding.UTF8,
            "application/json");
        var resp = await factory.CreateClient().PostAsync(
            "/api/vendor-webhooks/credit/validation", content);
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Oversized_body_is_413()
    {
        var content = new StringContent(
            new string('x', 70 * 1024), Encoding.UTF8,
            "application/json");
        var resp = await factory.CreateClient().PostAsync(
            "/api/vendor-webhooks/mpex/validation", content);
        resp.StatusCode.ShouldBe(HttpStatusCode
            .RequestEntityTooLarge);
    }
}
