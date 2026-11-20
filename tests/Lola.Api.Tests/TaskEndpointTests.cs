using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Audit;
using Lola.Application.Tasks;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// M16 P3 acceptance (API surface): ranked queue read + next-best,
/// audited claim/complete transitions, staff-only guards on REST and
/// GraphQL, cross-tenant isolation.
public sealed class TaskEndpointTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    // xunit builds a fresh class instance per test, so the shared
    // tenant + service token live in statics — /connect/token is under
    // the auth-tight rate limit (10/min/IP+path) and per-test
    // provisioning exhausts it. Tests still isolate via distinct
    // seeded tasks.
    private static (string Tenant, HttpClient Officer)? _staff;
    private static string? _svcToken;

    private async Task<(string Tenant, HttpClient Officer)> StaffAsync()
        => _staff ??= await ProvisionAsync();

    private async Task<(string Tenant, HttpClient Officer)> ProvisionAsync()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", _svcToken ??= await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Q Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(factory, tenant,
                LolaRole.LoanOfficer));
        return (tenant, officer);
    }

    private async Task<QueueTask> SeedTaskAsync(string tenant,
        string appId = "app-1",
        string kind = TaskKind.ConditionBlocking,
        int priority = TaskPriority.Normal,
        IReadOnlyList<string>? skills = null, string? due = null,
        string? refId = "cond-1")
        => await factory.Services
            .GetRequiredService<ITaskRepository>()
            .UpsertAsync(new TaskDraft(tenant, appId, kind,
                $"seeded {kind}", priority, skills ?? ["processing"],
                due, "condition", refId));

    private static async Task<string> ServiceTokenAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-svc",
                ["client_secret"] = "svc-secret-value"
            }));
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }

    [Fact]
    public async Task Queue_lists_ranked_and_next_returns_highest()
    {
        var (tenant, officer) = await StaffAsync();
        var low = await SeedTaskAsync(tenant,
            priority: TaskPriority.Low, refId: "a");
        var early = await SeedTaskAsync(tenant,
            priority: TaskPriority.High,
            due: "2026-01-05T00:00:00.000Z", refId: "b");
        var late = await SeedTaskAsync(tenant,
            priority: TaskPriority.High,
            due: "2026-01-10T00:00:00.000Z", refId: "c");
        var top = await SeedTaskAsync(tenant,
            priority: TaskPriority.Urgent, refId: "d");

        var mine = new[] { top.TaskId, early.TaskId, late.TaskId,
            low.TaskId }.ToHashSet();
        var list = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/tasks");
        var ids = list.EnumerateArray()
            .Select(t => t.GetProperty("taskId").GetString())
            .Where(id => id is not null && mine.Contains(id)).ToList();
        ids.ShouldBe([top.TaskId, early.TaskId, late.TaskId,
            low.TaskId]);

        var next = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/tasks/next");
        next.GetProperty("taskId").GetString().ShouldBe(top.TaskId);
    }

    [Fact]
    public async Task Skill_filter_narrows_the_queue()
    {
        var (tenant, officer) = await StaffAsync();
        var uw = await SeedTaskAsync(tenant,
            skills: ["underwriting"], refId: "a");
        var general = await SeedTaskAsync(tenant,
            skills: [], refId: "b");
        var closing = await SeedTaskAsync(tenant,
            skills: ["closing"], refId: "c");

        var list = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/tasks?skills=underwriting");
        var ids = list.EnumerateArray()
            .Select(t => t.GetProperty("taskId").GetString()).ToList();
        ids.ShouldContain(uw.TaskId);
        ids.ShouldContain(general.TaskId);   // untagged = general work
        ids.ShouldNotContain(closing.TaskId);
    }

    [Fact]
    public async Task Open_blocking_condition_surfaces_in_queue()
    {
        var (tenant, officer) = await StaffAsync();
        var app = await officer.PostAsJsonAsync(
            $"/api/tenants/{tenant}/applications", new { });
        var appId = (await app.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString()!;
        (await officer.PostAsJsonAsync(
            $"/api/tenants/{tenant}/applications/{appId}/conditions",
            new
            {
                kind = "doc_required",
                title = "Gift letter needed",
                blocking = true
            })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var list = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/tasks");
        var task = list.EnumerateArray().Single(t =>
            t.GetProperty("applicationId").GetString() == appId);
        task.GetProperty("kind").GetString()
            .ShouldBe(TaskKind.ConditionBlocking);
        task.GetProperty("skillTags").EnumerateArray()
            .Select(s => s.GetString()).ShouldBe(["processing"]);
    }

    [Fact]
    public async Task Claim_complete_arc_is_audited_and_leaves_queue()
    {
        var (tenant, officer) = await StaffAsync();
        var task = await SeedTaskAsync(tenant, refId: "arc");
        var url = $"/api/tenants/{tenant}/tasks/{task.TaskId}";

        var claim = await officer.PostAsync($"{url}/claim", null);
        claim.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await claim.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("state").GetString()
            .ShouldBe(TaskState.Claimed);

        // Second officer → 409 (claim is exclusive).
        await factory.SeedUserAsync(tenant, $"o2-{tenant}@test.dev",
            PkceLogin.Password, LolaRole.LoanOfficer);
        var other = factory.CreateClient();
        other.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant,
                LolaRole.LoanOfficer, email: $"o2-{tenant}@test.dev",
                seed: false));
        (await other.PostAsync($"{url}/claim", null)).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);

        var done = await officer.PostAsync($"{url}/complete", null);
        done.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await done.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("state").GetString().ShouldBe(TaskState.Done);

        // Done leaves the live queue.
        var list = await officer.GetFromJsonAsync<JsonElement>(
            $"/api/tenants/{tenant}/tasks");
        list.EnumerateArray().Select(
            t => t.GetProperty("taskId").GetString())
            .ShouldNotContain(task.TaskId);

        // Both transitions audited on the control-plane spine.
        var audit = factory.Services
            .GetRequiredService<IControlPlaneAuditLog>();
        var events = await audit.QueryAsync(
            new AuditFilter(Entity: "task", EntityId: task.TaskId));
        events.Select(e => e.Action).ShouldBe(
            ["task_claimed", "task_completed"], ignoreOrder: true);
    }

    [Fact]
    public async Task Borrower_denied_on_rest_and_graphql()
    {
        var (tenant, officer) = await StaffAsync();
        var task = await SeedTaskAsync(tenant);
        await factory.SeedUserAsync(tenant, $"b-{tenant}@test.dev",
            PkceLogin.Password, LolaRole.Borrower);
        var borrower = factory.CreateClient();
        borrower.DefaultRequestHeaders.Authorization = new("Bearer",
            await PkceLogin.TokenAsync(factory, tenant, LolaRole.Borrower,
                email: $"b-{tenant}@test.dev", seed: false));

        var base_ = $"/api/tenants/{tenant}/tasks";
        (await borrower.GetAsync(base_)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await borrower.GetAsync($"{base_}/next")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await borrower.PostAsync($"{base_}/{task.TaskId}/claim", null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        const string q = """
            query($t:String!){ tasks(tenantId:$t){ taskId }
                nextTask(tenantId:$t){ taskId } }
            """;
        var denied = await borrower.PostAsJsonAsync("/graphql",
            new { query = q, variables = new { t = tenant } });
        var json = await denied.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").GetProperty("tasks")
            .ValueKind.ShouldBe(JsonValueKind.Null);
        json.GetProperty("data").GetProperty("nextTask")
            .ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Cross_tenant_task_id_is_not_found()
    {
        var (tenantA, officerA) = await StaffAsync();
        var (tenantB, officerB) = await ProvisionAsync();
        var task = await SeedTaskAsync(tenantA, refId: "x");
        (await officerB.PostAsync(
            $"/api/tenants/{tenantB}/tasks/{task.TaskId}/claim",
            null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        // Tenant A still owns it.
        (await officerA.PostAsync(
            $"/api/tenants/{tenantA}/tasks/{task.TaskId}/claim",
            null)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
