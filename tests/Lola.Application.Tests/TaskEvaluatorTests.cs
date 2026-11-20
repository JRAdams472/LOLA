using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Application.Tasks;
using Lola.Domain.Applications;
using Shouldly;

namespace Lola.Application.Tests;

/// M16 P2 (MPE-254): tenant-side derivation into the PII-free queue.
/// FakeClock fixes "now" at 2026-01-05T12:00Z so TRID proximity and
/// draft-age thresholds are deterministic.
public sealed class TaskEvaluatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ApplicationMeta Meta(string id = "app-1",
        string status = "submitted", string? tridStart = null,
        string? updated = "2026-01-05T10:00:00.000Z")
        => new(id, status, "Validate", null, 1, tridStart, null, null,
            updated);

    private sealed class FakeApps(IReadOnlyList<ApplicationMeta> metas,
        IReadOnlyList<StepEventDto> steps) : IApplicationRepository
    {
        public Task<IReadOnlyList<ApplicationMeta>> ListAsync(
            CancellationToken ct = default) => Task.FromResult(metas);
        public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
            string id, CancellationToken ct = default)
            => Task.FromResult(steps);
        public Task InsertAsync(string id, ApplicationDocument doc,
            string status, string? borrowerUserId, string writtenBy,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> WriteVersionAsync(string id,
            ApplicationDocument doc, string status, string writtenBy,
            bool submitted, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<(int Version, ApplicationDocument Doc)> PatchDraftAsync(
            string id, IReadOnlyDictionary<string, string> patch,
            string writtenBy, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdateStatusAsync(string id, string status,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task EnsureTridClockStartAsync(string id,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdatePipelineStepAsync(string id, string step,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RecordStepEventAsync(string id, string step,
            string event_, string actor, string? reason,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationRetentionRow>>
            RetentionWindowsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ApplicationRecord?> GetAsync(string id,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private static Condition Cond(string id = "cond-1",
        string kind = ConditionKind.DocRequired,
        string state = ConditionState.Open, bool blocking = true,
        string title = "staff free text")
        => new(id, "app-1", kind, state, blocking, null, null, title,
            null, "officer", "2026-01-01T00:00:00.000Z", null, null, null);

    private static (TaskEvaluator Eval, FakeTaskRepository Tasks,
        FakeConditionRepository Conds) Rig(
            IReadOnlyList<ApplicationMeta>? metas = null,
            IReadOnlyList<StepEventDto>? steps = null,
            IReadOnlyList<Condition>? conds = null)
    {
        var tasks = new FakeTaskRepository();
        var condRepo = new FakeConditionRepository();
        if (conds is not null) condRepo.Store.AddRange(conds);
        var apps = new FakeApps(metas ?? [Meta()], steps ?? []);
        return (new TaskEvaluator(condRepo, apps, tasks, new FakeClock()),
            tasks, condRepo);
    }

    [Fact]
    public async Task Open_blocking_condition_produces_tagged_task()
    {
        var (eval, tasks, _) = Rig(conds: [Cond()]);
        await eval.EvaluateApplicationAsync("demo", "app-1");
        var t = tasks.Store.Single();
        t.Kind.ShouldBe(TaskKind.ConditionBlocking);
        t.BlockingEntity.ShouldBe(TaskSource.Condition);
        t.BlockingEntityId.ShouldBe("cond-1");
        t.SkillTags.ShouldBe(["processing"]);
        t.Priority.ShouldBe(TaskPriority.High);
        t.TenantId.ShouldBe("demo");
    }

    [Fact]
    public async Task Closing_condition_resolves_its_task()
    {
        var (eval, tasks, conds) = Rig(conds: [Cond()]);
        await eval.EvaluateApplicationAsync("demo", "app-1");
        tasks.Store.ShouldHaveSingleItem();
        conds.Store[0] = conds.Store[0] with
        { State = ConditionState.Closed };
        await eval.EvaluateApplicationAsync("demo", "app-1");
        tasks.Store.ShouldBeEmpty();
        tasks.Resolves.ShouldContain(("app-1", TaskSource.Condition,
            "cond-1"));
    }

    [Fact]
    public async Task Nonblocking_condition_emits_nothing()
    {
        var (eval, tasks, _) = Rig(conds: [Cond(blocking: false)]);
        await eval.EvaluateApplicationAsync("demo", "app-1");
        tasks.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reevaluation_dedupes_not_duplicates()
    {
        var (eval, tasks, _) = Rig(conds: [Cond()]);
        await eval.EvaluateApplicationAsync("demo", "app-1");
        await eval.EvaluateApplicationAsync("demo", "app-1");
        tasks.Store.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Trid_clock_produces_deadline_task()
    {
        // Clock stamped Fri Jan 2 → Reg Z deadline Mon Jan 5 00:00Z;
        // "now" is Jan 5 12:00Z — past deadline → Urgent.
        var (eval, tasks, _) = Rig(metas:
            [Meta(tridStart: "2026-01-02T10:00:00.000Z")]);
        await eval.EvaluateApplicationAsync("demo", "app-1");
        var t = tasks.Store.Single();
        t.Kind.ShouldBe(TaskKind.TridDeadline);
        t.DueUtc.ShouldBe(TridClock.DeadlineUtc(
            "2026-01-02T10:00:00.000Z"));
        t.Priority.ShouldBe(TaskPriority.Urgent);
        t.SkillTags.ShouldBe([TaskSkills.Closing]);
    }

    [Fact]
    public async Task Trid_task_resolves_on_decision()
    {
        var (eval, tasks, _) = Rig(metas:
            [Meta(tridStart: "2026-01-02T10:00:00.000Z")]);
        await eval.EvaluateApplicationAsync("demo", "app-1");
        tasks.Store.ShouldHaveSingleItem();
        var (eval2, tasks2, _) = Rig(metas:
            [Meta(status: "decisioned",
                tridStart: "2026-01-02T10:00:00.000Z")]);
        tasks2.Store.AddRange(tasks.Store);
        await eval2.EvaluateApplicationAsync("demo", "app-1");
        tasks2.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stale_draft_ages_into_queue_fresh_does_not()
    {
        var (eval, tasks, _) = Rig(metas:
            [Meta(status: "draft",
                updated: "2025-12-25T10:00:00.000Z")]);
        await eval.EvaluateApplicationAsync("demo", "app-1");
        var t = tasks.Store.Single();
        t.Kind.ShouldBe(TaskKind.StaleDraft);
        t.Priority.ShouldBe(TaskPriority.Low);
        t.SkillTags.ShouldBe([TaskSkills.Processing]);

        var (eval2, tasks2, _) = Rig(metas:
            [Meta(status: "draft",
                updated: "2026-01-05T10:00:00.000Z")]);
        await eval2.EvaluateApplicationAsync("demo", "app-1");
        tasks2.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failed_step_event_produces_review_then_resolves()
    {
        StepEventDto Ev(string ev, string ts) => new($"se-{ts}",
            "Credit", ev, "vendor", null, ts);
        var (eval, tasks, _) = Rig(steps:
            [Ev("ordered", "2026-01-04T00:00:00.000Z"),
             Ev("failed", "2026-01-04T01:00:00.000Z")]);
        await eval.EvaluateApplicationAsync("demo", "app-1");
        var t = tasks.Store.Single();
        t.Kind.ShouldBe(TaskKind.StepReview);
        t.BlockingEntity.ShouldBe(TaskSource.StepEvent);
        t.BlockingEntityId.ShouldBe("Credit");

        var (eval2, tasks2, _) = Rig(steps:
            [Ev("ordered", "2026-01-04T00:00:00.000Z"),
             Ev("failed", "2026-01-04T01:00:00.000Z"),
             Ev("completed", "2026-01-04T02:00:00.000Z")]);
        tasks2.Store.AddRange(tasks.Store);
        await eval2.EvaluateApplicationAsync("demo", "app-1");
        tasks2.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task Task_titles_are_generated_not_staff_free_text()
    {
        // Row-inspection analog: a staff-supplied condition title
        // (which may name a borrower) must never reach the queue row.
        var (eval, tasks, _) = Rig(conds:
            [Cond(title: "Call borrower Jane Doe re: docs")]);
        await eval.EvaluateApplicationAsync("demo", "app-1");
        tasks.Store.Single().Title.ShouldBe(
            "Resolve blocking condition — doc_required");
    }

    [Fact]
    public async Task Tenant_eval_covers_every_app()
    {
        var (eval, tasks, conds) = Rig(metas:
            [Meta(id: "app-1", status: "draft",
                  updated: "2025-12-25T10:00:00.000Z"),
             Meta(id: "app-2")]);
        conds.Store.Add(Cond(id: "cond-9"));
        await eval.EvaluateTenantAsync("demo");
        tasks.Store.Select(t => t.ApplicationId)
            .ShouldBe(["app-1", "app-1"], ignoreOrder: true);
        tasks.Store.ShouldContain(t =>
            t.ApplicationId == "app-1"
            && t.Kind == TaskKind.ConditionBlocking);
        tasks.Store.ShouldContain(t =>
            t.ApplicationId == "app-1" && t.Kind == TaskKind.StaleDraft);
    }
}
