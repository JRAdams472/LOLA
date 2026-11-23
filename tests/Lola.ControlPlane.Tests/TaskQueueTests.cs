using Dapper;
using Lola.Application.Tasks;
using Lola.ControlPlane;
using Shouldly;

namespace Lola.ControlPlane.Tests;

/// M16 P1 (MPE-253): tasks schema + TaskQueue behavior. The column
/// whitelist test is the PII guard — adding any borrower-capable
/// column to `tasks` fails it.
public sealed class TaskQueueTests
{
    // The exact columns `tasks` may ever have — refs + operational
    // metadata only. Any new column fails this set equality until a
    // human reviews it against the PII boundary.
    private static readonly string[] AllowedColumns =
    [
        "seq", "task_id", "tenant_id", "application_id", "kind",
        "title", "priority", "skill_tags", "due_utc", "state",
        "blocking_entity", "blocking_entity_id", "claimed_by",
        "created_utc", "claimed_utc", "completed_utc", "updated_utc"
    ];

    private static ControlPlaneDb TempDb()
    {
        var db = new ControlPlaneDb(Path.Combine(Path.GetTempPath(),
            "lola-tests", $"tq-{Guid.NewGuid():N}.db"), TestDbKeys.ControlFileKeyHex);
        db.Migrate();
        return db;
    }

    private static TaskDraft Draft(string tenantId = "demo",
        string appId = "app-1", string kind = TaskKind.ConditionBlocking,
        string title = "Resolve blocking condition",
        int priority = TaskPriority.High,
        IReadOnlyList<string>? skills = null,
        string? due = null, string? entity = TaskSource.Condition,
        string? entityId = "cond-1")
        => new(tenantId, appId, kind, title, priority,
            skills ?? ["underwriting"], due, entity, entityId);

    [Fact]
    public void Migrate_creates_tasks_table_with_exact_column_set()
    {
        var db = TempDb();
        using var conn = db.Open();
        var cols = conn.Query<string>(
            "SELECT name FROM pragma_table_info('tasks')").ToList();
        cols.ShouldBe(AllowedColumns.ToHashSet(),
            ignoreOrder: true);
        var indexes = conn.Query<string>(
            """
            SELECT name FROM sqlite_master
            WHERE type = 'index' AND tbl_name = 'tasks'
            """).ToList();
        indexes.ShouldContain("ix_tasks_queue");
        indexes.ShouldContain("ix_tasks_application");
        indexes.ShouldContain("ux_tasks_live_source");
    }

    [Fact]
    public async Task Upsert_inserts_then_refreshes_metadata_in_place()
    {
        var q = new TaskQueue(TempDb());
        var first = await q.UpsertAsync(Draft());
        first.State.ShouldBe(TaskState.Open);
        first.TaskId.ShouldStartWith("task-");

        var again = await q.UpsertAsync(Draft(
            priority: TaskPriority.Urgent, title: "new title"));
        again.TaskId.ShouldBe(first.TaskId);          // dedupe, no dup row
        again.Priority.ShouldBe(TaskPriority.Urgent);
        again.Title.ShouldBe("new title");

        var queue = await q.ListQueueAsync("demo");
        queue.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reupsert_does_not_regress_claim_state()
    {
        var q = new TaskQueue(TempDb());
        var t = await q.UpsertAsync(Draft());
        await q.ClaimAsync(t.TaskId, "officer@lola.dev");
        var again = await q.UpsertAsync(Draft(priority: TaskPriority.Low));
        again.State.ShouldBe(TaskState.Claimed);
        again.ClaimedBy.ShouldBe("officer@lola.dev");
        again.ClaimedUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task Claim_is_exclusive_second_claim_returns_null()
    {
        var q = new TaskQueue(TempDb());
        var t = await q.UpsertAsync(Draft());
        var claimed = await q.ClaimAsync(t.TaskId, "a@lola.dev");
        claimed.ShouldNotBeNull();
        claimed.State.ShouldBe(TaskState.Claimed);
        claimed.ClaimedBy.ShouldBe("a@lola.dev");
        (await q.ClaimAsync(t.TaskId, "b@lola.dev")).ShouldBeNull();
    }

    [Fact]
    public async Task Complete_marks_done_and_leaves_the_queue()
    {
        var q = new TaskQueue(TempDb());
        var t = await q.UpsertAsync(Draft());
        await q.ClaimAsync(t.TaskId, "a@lola.dev");
        var done = await q.CompleteAsync(t.TaskId, "a@lola.dev");
        done.ShouldNotBeNull();
        done.State.ShouldBe(TaskState.Done);
        done.CompletedUtc.ShouldNotBeNull();
        (await q.CompleteAsync(t.TaskId, "a@lola.dev")).ShouldBeNull();
        (await q.ListQueueAsync("demo")).ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveBySource_cancels_live_tasks_only()
    {
        var q = new TaskQueue(TempDb());
        var t = await q.UpsertAsync(Draft(entityId: "cond-1"));
        var other = await q.UpsertAsync(Draft(entityId: "cond-2"));
        var resolved = await q.ResolveBySourceAsync(
            "demo", "app-1", TaskSource.Condition, "cond-1");
        resolved.ShouldBe(1);
        var queue = await q.ListQueueAsync("demo");
        queue.Select(x => x.TaskId).ShouldBe([other.TaskId]);
        (await q.GetAsync(t.TaskId))!.State.ShouldBe(TaskState.Canceled);
    }

    [Fact]
    public async Task Queue_ranks_priority_desc_then_due_asc()
    {
        var q = new TaskQueue(TempDb());
        var late = await q.UpsertAsync(Draft(
            priority: TaskPriority.High, due: "2026-01-10T00:00:00.000Z",
            entityId: "a"));
        var early = await q.UpsertAsync(Draft(
            priority: TaskPriority.High, due: "2026-01-05T00:00:00.000Z",
            entityId: "b"));
        var top = await q.UpsertAsync(Draft(
            priority: TaskPriority.Urgent, entityId: "c"));
        var noDue = await q.UpsertAsync(Draft(
            priority: TaskPriority.High, entityId: "d"));
        var queue = await q.ListQueueAsync("demo");
        queue.Select(t => t.TaskId).ShouldBe(
            [top.TaskId, early.TaskId, late.TaskId, noDue.TaskId]);
    }

    // PII-shaped tokens that must never appear in a tasks row —
    // SSN, email, phone, DOB-ish dates, name-ish free text.
    private static readonly string[] PiiTokens =
    [
        "111-22-3333", "123-45-6789", "@lola.dev", "@example.com",
        "555-1212", "(555)", "Jane Doe", "John Smith", "1985-04-12"
    ];

    [Fact]
    public async Task Row_level_pii_scan_finds_nothing()
    {
        var db = TempDb();
        var q = new TaskQueue(db);
        await q.UpsertAsync(Draft(title: "Resolve blocking condition"));
        var claimed = await q.ClaimAsync(
            (await q.ListQueueAsync("demo"))[0].TaskId, "officer-1");
        claimed.ShouldNotBeNull();
        using var conn = db.Open();
        var row = conn.QuerySingle(
            "SELECT * FROM tasks WHERE task_id = @id",
            new { id = claimed.TaskId });
        var flat = string.Join('|',
            ((IDictionary<string, object?>)row).Values
                .Select(v => v?.ToString() ?? ""));
        foreach (var token in PiiTokens)
            flat.ShouldNotContain(token, Case.Insensitive);
        // claimed_by carries an actor id (u-…/email-scoped id), never
        // a borrower attribute — assert it's the id we wrote.
        flat.ShouldContain("officer-1");
    }

    [Fact]
    public async Task Skill_filter_matches_tagged_and_untagged()
    {
        var q = new TaskQueue(TempDb());
        var uw = await q.UpsertAsync(Draft(
            skills: ["underwriting"], entityId: "a"));
        var proc = await q.UpsertAsync(Draft(
            skills: ["processing"], entityId: "b"));
        var general = await q.UpsertAsync(Draft(
            skills: [], entityId: "c"));
        var forUw = await q.ListQueueAsync("demo", ["underwriting"]);
        forUw.Select(t => t.TaskId).ShouldBe(
            [uw.TaskId, general.TaskId], ignoreOrder: true);
        (await q.ListQueueAsync("demo")).Count.ShouldBe(3);
    }
}
