using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Events;
using Lola.Application.Tasks;
using Lola.Application.Vendors;
using Lola.Domain.Applications;
using Shouldly;

namespace Lola.Application.Tests;

/// M17 P2 (MPE-258): finding→domain mapping on fakes — the ledger
/// decides replay before any write; `cleared` auto-closes its linked
/// open condition; exception findings open blocking conditions that
/// the evaluator surfaces as `validation_exception` tasks.
public sealed class RecordValidationFeedbackTests
{
    private sealed class FakeFeedback : IVendorFeedbackRepository
    {
        public List<VendorFeedbackReceipt> Rows { get; } = [];
        public Task<bool> TryRecordAsync(VendorFeedbackReceipt receipt,
            CancellationToken ct = default)
        {
            if (Rows.Any(r => r.Source == receipt.Source
                && r.FeedbackId == receipt.FeedbackId))
                return Task.FromResult(false);
            Rows.Add(receipt);
            return Task.FromResult(true);
        }
        public Task<VendorFeedbackRow?> GetAsync(string source,
            string feedbackId, CancellationToken ct = default)
            => Task.FromResult(Rows
                .Where(r => r.Source == source
                    && r.FeedbackId == feedbackId)
                .Select(r => new VendorFeedbackRow(r.Source,
                    r.ApplicationId, r.FeedbackId, r.Finding,
                    r.Severity, r.LinkedEntity, r.LinkedEntityId,
                    "2026-01-01T00:00:00.000Z"))
                .FirstOrDefault());
        public Task<IReadOnlyList<VendorFeedbackRow>>
            ListByApplicationAsync(string applicationId,
                CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<VendorFeedbackRow>>([]);
    }

    private sealed class FakeApps(ApplicationRecord record)
        : IApplicationRepository
    {
        public Task<ApplicationRecord?> GetAsync(string id,
            CancellationToken ct = default)
            => Task.FromResult<ApplicationRecord?>(
                id == record.Id ? record : null);
        public Task<IReadOnlyList<ApplicationMeta>> ListAsync(
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ApplicationMeta>>(
                [new ApplicationMeta(record.Id, record.Status,
                    "Validate", null, 1, null, null, null,
                    "2026-01-05T10:00:00.000Z")]);
        public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
            string id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StepEventDto>>([]);
        public Task InsertAsync(string id, ApplicationDocument doc,
            string status, string? borrowerUserId, string writtenBy,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> WriteVersionAsync(string id,
            ApplicationDocument doc, string status, string writtenBy,
            bool submitted, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<(int Version, ApplicationDocument Doc)>
            PatchDraftAsync(string id,
                IReadOnlyDictionary<string, string> patch,
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
        public Task<IReadOnlyList<string>> SearchAsync(string field,
            string value, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class CapturingAudit : ITenantAuditLog
    {
        public List<(string Action, string Entity, string? EntityId)>
            Rows
        { get; } = [];
        public Task WriteAsync(string actorId, string action,
            string entity, string? entityId,
            IReadOnlyList<string> fieldClasses, string? reason,
            CancellationToken ct = default)
        {
            Rows.Add((action, entity, entityId));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<AuditEvent>> QueryAsync(
            AuditFilter filter, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<AuditVerifyResult> VerifyChainAsync(
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private static ValidationFeedback Feedback(
        string finding = "cleared", string severity = "info",
        string source = "mpex", string? conditionId = "cond-1",
        string feedbackId = "vf-1")
        => new(source, "app-1", feedbackId, finding, severity,
            conditionId, null, null);

    private static Condition OpenCond(string id = "cond-1",
        string state = ConditionState.Open)
        => new(id, "app-1", ConditionKind.DataValidation, state, true,
            FeedbackLink.VendorFeedback, "vf-0", "title", null,
            "auto:mpex", "2026-01-01T00:00:00.000Z", null, null, null);

    private static (FakeFeedback Feedback, FakeConditionRepository Conds,
        CapturingAudit Audit, FakeEventLog Events,
        FakeTaskRepository Tasks) Rig(
            IReadOnlyList<Condition> conds,
            out Func<RecordValidationFeedbackCommand,
                CancellationToken, Task<FeedbackOutcome>>? invoke)
    {
        var feedback = new FakeFeedback();
        var condRepo = new FakeConditionRepository();
        if (conds is not null) condRepo.Store.AddRange(conds);
        var apps = new FakeApps(new ApplicationRecord("app-1",
            "submitted", "POSIntake", null, 1, null, null,
            ApplicationDocument.Empty()));
        var audit = new CapturingAudit();
        var events = new FakeEventLog();
        var tasks = new FakeTaskRepository();
        var eval = new TaskEvaluator(condRepo, apps, tasks,
            TimeProvider.System);
        invoke = async (c, ct) =>
        {
            using var _ = Lola.Application.Tenancy.TenantContext.Use(
                new Lola.Application.Tenancy.TenantScope(
                    c.TenantId, "x.db", "k"));
            return await RecordValidationFeedbackHandler.Handle(
                c, feedback, condRepo, apps, audit, events, eval, ct);
        };
        return (feedback, condRepo, audit, events, tasks);
    }

    [Fact]
    public async Task Cleared_finding_auto_closes_the_linked_condition()
    {
        var (feedback, conds, audit, events, _) =
            Rig([OpenCond()], out var invoke);

        var outcome = await invoke!(new RecordValidationFeedbackCommand(
            "demo", Feedback()), default);

        outcome.Action.ShouldBe("condition_closed");
        conds.Store.Single().State.ShouldBe(ConditionState.Closed);
        events.Drafts.Select(d => d.EventKind).ShouldBe(
            [LoanEventKind.ValidationReceived,
             LoanEventKind.ConditionClosed]);
        audit.Rows.Select(r => r.Action).ShouldBe(
            ["validation_received", "condition_closed"]);
    }

    [Fact]
    public async Task Closing_condition_resolves_its_queue_task()
    {
        // Seed an open feedback condition. The first receipt (a
        // non-blocking advisory) still runs the eval pass, which
        // derives a validation_exception task for the seeded blocker.
        var (_, conds, _, _, tasks) = Rig([OpenCond()], out var invoke);
        await invoke!(new RecordValidationFeedbackCommand("demo",
            Feedback(finding: "missing_field", severity: "warning",
                conditionId: null, feedbackId: "vf-0")), default);
        tasks.Store.Single().Kind.ShouldBe(TaskKind.ValidationException);

        // The clear closes the seeded condition; the same eval pass
        // resolves its task convergently.
        await invoke!(new RecordValidationFeedbackCommand("demo",
            Feedback(conditionId: "cond-1", feedbackId: "vf-2")), default);

        conds.Store[0].State.ShouldBe(ConditionState.Closed);
        tasks.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task Exception_finding_opens_condition_and_queue_task()
    {
        var (feedback, conds, audit, events, tasks) =
            Rig([], out var invoke);

        var outcome = await invoke!(new RecordValidationFeedbackCommand(
            "demo", Feedback(finding: "exception", severity: "error",
                source: "isppr", conditionId: null)), default);

        outcome.Action.ShouldBe("condition_opened");
        var cond = conds.Store.Single();
        cond.Kind.ShouldBe(ConditionKind.DataValidation);
        cond.Blocking.ShouldBeTrue();
        cond.LinkedEntity.ShouldBe(FeedbackLink.VendorFeedback);
        cond.LinkedEntityId.ShouldBe("vf-1");
        cond.OpenedBy.ShouldBe("auto:isppr");

        var task = tasks.Store.Single();
        task.Kind.ShouldBe(TaskKind.ValidationException);
        task.Priority.ShouldBe(TaskPriority.High);
        task.SkillTags.ShouldBe(["processing"]);   // isppr → processing
        task.BlockingEntity.ShouldBe(TaskSource.Condition);
        task.BlockingEntityId.ShouldBe(cond.ConditionId);

        events.Drafts.Select(d => d.EventKind).ShouldBe(
            [LoanEventKind.ValidationReceived,
             LoanEventKind.ConditionOpened]);
        audit.Rows.Select(r => r.Action).ShouldBe(
            ["validation_received", "condition_opened"]);
    }

    [Fact]
    public async Task Mpex_exception_maps_to_underwriting_skill()
    {
        var (_, _, _, _, tasks) = Rig([], out var invoke);
        await invoke!(new RecordValidationFeedbackCommand("demo",
            Feedback(finding: "exception", severity: "error",
                source: "mpex", conditionId: null)), default);
        tasks.Store.Single().SkillTags.ShouldBe(["underwriting"]);
    }

    [Fact]
    public async Task Advisory_finding_opens_nonblocking_condition_no_task()
    {
        var (_, conds, _, _, tasks) = Rig([], out var invoke);
        await invoke!(new RecordValidationFeedbackCommand("demo",
            Feedback(finding: "missing_field", severity: "warning",
                conditionId: null)), default);

        conds.Store.Single().Blocking.ShouldBeFalse();
        tasks.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cleared_without_matching_condition_still_records()
    {
        var (_, conds, _, events, _) = Rig([], out var invoke);
        var outcome = await invoke!(new RecordValidationFeedbackCommand(
            "demo", Feedback(conditionId: "cond-999")), default);

        outcome.Action.ShouldBe("recorded");
        conds.Store.ShouldBeEmpty();
        events.Drafts.ShouldHaveSingleItem().EventKind
            .ShouldBe(LoanEventKind.ValidationReceived);
    }

    [Fact]
    public async Task Replay_is_a_noop_before_any_domain_write()
    {
        var (feedback, conds, audit, events, tasks) =
            Rig([OpenCond()], out var invoke);
        var cmd = new RecordValidationFeedbackCommand("demo", Feedback());
        await invoke!(cmd, default);
        var draftsBefore = events.Drafts.Count;

        var outcome = await invoke!(cmd, default);

        outcome.Replay.ShouldBeTrue();
        feedback.Rows.ShouldHaveSingleItem();
        events.Drafts.Count.ShouldBe(draftsBefore);   // no second event
        audit.Rows.Count.ShouldBe(2);                 // unchanged
        tasks.Store.ShouldBeEmpty();                  // nothing derived
    }

    [Fact]
    public async Task Unknown_finding_is_rejected()
        => await Should.ThrowAsync<DomainException>(() =>
        {
            var rig = Rig([], out var invoke);
            return invoke!(new RecordValidationFeedbackCommand("demo",
                Feedback(finding: "bogus")), default);
        });
}
