using Lola.Application.Applications;
using Lola.Application.Compliance;
using Lola.Application.Errors;
using Lola.Application.Events;
using Lola.Application.Fees;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Domain.Applications;
using Lola.Domain.Compliance;
using Shouldly;
using Xunit;

namespace Lola.Application.Tests;

/// M21 P2 (MPE-298) — version binding, reset/cure semantics, and the
/// evaluate-on-completion wiring over `ToleranceEvaluator`.
public sealed class ToleranceTests
{
    // -- fakes ------------------------------------------------------

    private sealed class FakeTolerance : IToleranceRepository
    {
        public List<DisclosureVersionRecord> Versions { get; } = [];
        public List<ToleranceResetRecord> Resets { get; } = [];
        public Dictionary<string, ToleranceClassRecord> Classes { get; }
            = new();

        public Task<DisclosureVersionRecord> AddVersionAsync(
            string appId, string kind, string evalId, string? docId,
            string? resetOfVersionId, CancellationToken ct)
        {
            var v = new DisclosureVersionRecord(
                $"dver-{Versions.Count + 1}", appId, kind, evalId, docId,
                Versions.Count(v2 => v2.Kind == kind) + 1,
                resetOfVersionId, "2026-01-01T00:00:00.000Z");
            Versions.Add(v);
            foreach (var r in Resets.Where(
                r => r.RebasedVersionId is null).ToList())
                Resets[Resets.IndexOf(r)] = r with
                { RebasedVersionId = v.VersionId };
            return Task.FromResult(v);
        }

        public Task<IReadOnlyList<DisclosureVersionRecord>>
            ListVersionsAsync(string appId, string kind,
                CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DisclosureVersionRecord>>(
                Versions.Where(v => v.Kind == kind)
                    .OrderBy(v => v.Seq).ToList());

        public Task<DisclosureVersionRecord?> GetVersionByDocAsync(
            string docId, CancellationToken ct)
            => Task.FromResult(Versions
                .FirstOrDefault(v => v.DocId == docId));

        public Task<IReadOnlyDictionary<string, ToleranceClassRecord>>
            GetClassesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string,
                ToleranceClassRecord>>(Classes);

        public Task<ToleranceResetRecord> AddResetAsync(string appId,
            string reasonCode, string feeCodesJson, string actor,
            CancellationToken ct)
        {
            var r = new ToleranceResetRecord(
                $"tres-{Resets.Count + 1}", appId, reasonCode,
                feeCodesJson, null, actor, "2026-01-01T00:00:00.000Z");
            Resets.Add(r);
            return Task.FromResult(r);
        }

        public Task<IReadOnlyList<ToleranceResetRecord>> ListResetsAsync(
            string appId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ToleranceResetRecord>>(
                Resets.ToList());

        public Task StampResetAsync(string resetId,
            string rebasedVersionId, CancellationToken ct)
        {
            var i = Resets.FindIndex(r => r.ResetId == resetId);
            if (i >= 0)
                Resets[i] = Resets[i] with
                { RebasedVersionId = rebasedVersionId };
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFees : IFeeRepository
    {
        public Dictionary<string, List<FeeItemRecord>> Items { get; }
            = new();
        public List<FeeOverrideRecord> Overrides { get; } = [];
        public Dictionary<string, FeeEvalRecord> Evals { get; } = new();

        public Task<FeeItemLookup?> GetItemAsync(string itemId,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<FeeItemRecord>> ListItemsAsync(
            string evalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeItemRecord>>(
                Items.GetValueOrDefault(evalId) ?? []);
        public Task<FeeEvalRecord?> GetEvalAsync(string evalId,
            CancellationToken ct = default)
            => Task.FromResult(Evals.GetValueOrDefault(evalId));
        public Task<IReadOnlyList<FeeOverrideRecord>>
            ListOverridesAsync(string appId,
                CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeOverrideRecord>>(
                Overrides);
        public Task<FeeEvalRecord?> GetLatestEvalAsync(string appId,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<FeeEvalRecord>> ListEvalsAsync(
            string appId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<FeeEnqueueResult> EnqueueAsync(FeeEvalRecord eval,
            string outboxId, string kind, string payloadJson,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<FeeOutboxRow>> ListDueOutboxAsync(
            string nowUtc, int limit, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task CompleteAsync(string outboxId, string evalId,
            string adapter, string resultJson,
            IReadOnlyList<FeeItemRecord> items, string completedUtc,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RescheduleAsync(string outboxId, string evalId,
            long attempts, string nextRetryUtc, string error,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task DeadLetterAsync(string outboxId, string evalId,
            long attempts, string error, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<FeeOverrideRecord?> GetOverrideAsync(
            string overrideId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<FeeOverrideRecord> UpsertOverrideAsync(
            FeeOverrideRecord o, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task ClearOverrideAsync(string overrideId,
            string clearedUtc, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<FeeEvalRecord>>
            GetLatestBatchEvalsAsync(string appId,
                CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeApps : IApplicationRepository
    {
        public Task<ApplicationRecord?> GetAsync(string id,
            CancellationToken ct = default)
            => Task.FromResult<ApplicationRecord?>(new(id, "draft",
                "intake", null, 1, null, null,
                ApplicationDocument.Empty()));
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
        public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
            string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationRetentionRow>>
            RetentionWindowsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationMeta>> ListAsync(
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> IsPartnerLinkedAsync(string appId,
            string partnerUserId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<string>>
            LinkedAppIdsForPartnerAsync(string partnerUserId,
                CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ApplicationPage> ListScopedAsync(
            ActorScope scope, string? actorId, int skip, int take,
            bool includeNames, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ScopedApplicationMeta?> GetScopedMetaAsync(
            string id, ActorScope scope, string? actorId,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<string?> GetDocValueAsync(string id, string path,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task LinkAsync(PartnerLink link,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> RevokeAsync(string appId,
            string partnerUserId, string revokedUtc,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<PartnerLink>> ListForAppAsync(
            string appId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<PartnerLinkOutcome> CommitLinkAsync(
            PartnerLink link, Audit.AuditDraft audit,
            Events.LoanEventDraft loanEvent,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<PartnerLinkOutcome> CommitRevokeAsync(string appId,
            string partnerUserId, string revokedUtc,
            Audit.AuditDraft audit, Events.LoanEventDraft loanEvent,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private static IDisposable Scope()
        => TenantContext.Use(new TenantScope("t1", "x.db", "k"));

    private static FeeItemRecord Item(string evalId, string code,
        string section, double total, string? paidTo = null,
        long? fromList = null)
        => new($"it-{evalId}-{code}", evalId, code, code, section,
            total, "[]", total, "2026-01-01T00:00:00.000Z",
            paidTo, fromList);

    private static DisclosureVersionRecord Ver(string id, int seq,
        string evalId, string kind = "le")
        => new(id, "app-1", kind, evalId, $"doc-{seq}", seq, null,
            "2026-01-01T00:00:00.000Z");

    // -- EvaluateDisclosureTolerance ---------------------------------

    [Fact]
    public async Task Seq1_is_baseline_no_diff_no_violation()
    {
        var tol = new FakeTolerance();
        var fees = new FakeFees();
        var conds = new FakeConditionRepository();
        var events = new FakeEventLog();
        var v1 = await tol.AddVersionAsync("app-1", "le", "e1",
            "doc-1", null, default);
        fees.Items["e1"] = [Item("e1", "origination", "A", 900)];

        await EvaluateDisclosureTolerance.Handle("app-1", "doc-1",
            "actor", tol, fees, conds, events, default);

        events.Drafts.ShouldBeEmpty();
        conds.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task Violation_writes_event_and_one_blocking_condition()
    {
        var tol = new FakeTolerance();
        var fees = new FakeFees();
        var conds = new FakeConditionRepository();
        var events = new FakeEventLog();
        await tol.AddVersionAsync("app-1", "le", "e1", "doc-1", null,
            default);
        await tol.AddVersionAsync("app-1", "le", "e2", "doc-2", null,
            default);
        fees.Items["e1"] = [Item("e1", "origination", "A", 900)];
        fees.Items["e2"] = [Item("e2", "origination", "A", 950)];
        tol.Classes["origination"] =
            new("origination", "zero", false);

        await EvaluateDisclosureTolerance.Handle("app-1", "doc-2",
            "system:doc-drain", tol, fees, conds, events, default);

        var draft = events.Drafts.ShouldHaveSingleItem();
        draft.EventKind.ShouldBe(LoanEventKind.ToleranceViolation);
        var cond = conds.Store.ShouldHaveSingleItem();
        cond.Kind.ShouldBe(ConditionKind.ToleranceCure);
        cond.Blocking.ShouldBeTrue();
    }

    [Fact]
    public async Task Open_cure_condition_dedupes()
    {
        var tol = new FakeTolerance();
        var fees = new FakeFees();
        var conds = new FakeConditionRepository();
        var events = new FakeEventLog();
        await tol.AddVersionAsync("app-1", "le", "e1", "doc-1", null,
            default);
        await tol.AddVersionAsync("app-1", "le", "e2", "doc-2", null,
            default);
        await tol.AddVersionAsync("app-1", "le", "e3", "doc-3", null,
            default);
        fees.Items["e1"] = [Item("e1", "origination", "A", 900)];
        fees.Items["e2"] = [Item("e2", "origination", "A", 950)];
        fees.Items["e3"] = [Item("e3", "origination", "A", 990)];
        tol.Classes["origination"] =
            new("origination", "zero", false);

        await EvaluateDisclosureTolerance.Handle("app-1", "doc-2",
            "actor", tol, fees, conds, events, default);
        await EvaluateDisclosureTolerance.Handle("app-1", "doc-3",
            "actor", tol, fees, conds, events, default);

        events.Drafts.Count(d =>
            d.EventKind == LoanEventKind.ToleranceViolation)
            .ShouldBe(2);
        conds.Store.ShouldHaveSingleItem(); // still just the one
    }

    [Fact]
    public async Task Reset_covered_fee_self_baselines_no_violation()
    {
        var tol = new FakeTolerance();
        var fees = new FakeFees();
        var conds = new FakeConditionRepository();
        var events = new FakeEventLog();
        await tol.AddVersionAsync("app-1", "le", "e1", "doc-1", null,
            default);
        // Officer records a changed circumstance covering origination…
        await tol.AddResetAsync("app-1", "changed_info",
            "[\"origination\"]", "officer-1", default);
        // …then issues the revised LE — the reset stamps to v2.
        await tol.AddVersionAsync("app-1", "le", "e2", "doc-2", null,
            default);
        fees.Items["e1"] = [Item("e1", "origination", "A", 900)];
        fees.Items["e2"] = [Item("e2", "origination", "A", 1200)];
        tol.Classes["origination"] =
            new("origination", "zero", false);

        await EvaluateDisclosureTolerance.Handle("app-1", "doc-2",
            "actor", tol, fees, conds, events, default);

        events.Drafts.ShouldBeEmpty();
        conds.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task Uncovered_fees_still_violate_after_reset()
    {
        var tol = new FakeTolerance();
        var fees = new FakeFees();
        var conds = new FakeConditionRepository();
        var events = new FakeEventLog();
        await tol.AddVersionAsync("app-1", "le", "e1", "doc-1", null,
            default);
        await tol.AddResetAsync("app-1", "changed_info",
            "[\"origination\"]", "officer-1", default);
        await tol.AddVersionAsync("app-1", "le", "e2", "doc-2", null,
            default);
        fees.Items["e1"] =
        [
            Item("e1", "origination", "A", 900),
            Item("e1", "credit-report", "B", 45),
        ];
        fees.Items["e2"] =
        [
            Item("e2", "origination", "A", 1200), // rebased — clean
            Item("e2", "credit-report", "B", 60), // uncovered — violates
        ];
        tol.Classes["origination"] =
            new("origination", "zero", false);
        tol.Classes["credit-report"] =
            new("credit-report", "zero", false);

        await EvaluateDisclosureTolerance.Handle("app-1", "doc-2",
            "actor", tol, fees, conds, events, default);

        events.Drafts.ShouldHaveSingleItem().EventKind
            .ShouldBe(LoanEventKind.ToleranceViolation);
        conds.Store.ShouldHaveSingleItem();
    }

    // -- RecordChangedCircumstance ------------------------------------

    [Fact]
    public async Task Valid_reset_persists_and_writes_event()
    {
        using var _ = Scope();
        var tol = new FakeTolerance();
        var events = new FakeEventLog();
        await tol.AddVersionAsync("app-1", "le", "e1", "doc-1", null,
            default);

        var reset = await RecordChangedCircumstanceHandler.Handle(
            new RecordChangedCircumstanceCommand("t1", "app-1",
                "officer-1", ActorScope.Staff, "borrower_request",
                ["appraisal", "survey"]),
            new FakeApps(), tol, events, default);

        tol.Resets.ShouldHaveSingleItem().ResetId
            .ShouldBe(reset.ResetId);
        events.Drafts.ShouldHaveSingleItem().EventKind
            .ShouldBe(LoanEventKind.ToleranceReset);
    }

    [Fact]
    public async Task Invalid_reason_refused()
    {
        using var _ = Scope();
        var tol = new FakeTolerance();
        await tol.AddVersionAsync("app-1", "le", "e1", "doc-1", null,
            default);
        await Should.ThrowAsync<DomainException>(async () =>
            await RecordChangedCircumstanceHandler.Handle(
                new RecordChangedCircumstanceCommand("t1", "app-1",
                    "officer-1", ActorScope.Staff, "vibes",
                    ["appraisal"]),
                new FakeApps(), tol, new FakeEventLog(), default));
        tol.Resets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Empty_fee_list_refused()
    {
        using var _ = Scope();
        var tol = new FakeTolerance();
        await tol.AddVersionAsync("app-1", "le", "e1", "doc-1", null,
            default);
        await Should.ThrowAsync<DomainException>(async () =>
            await RecordChangedCircumstanceHandler.Handle(
                new RecordChangedCircumstanceCommand("t1", "app-1",
                    "officer-1", ActorScope.Staff, "changed_info", []),
                new FakeApps(), tol, new FakeEventLog(), default));
    }

    [Fact]
    public async Task Reset_without_baseline_refused()
    {
        using var _ = Scope();
        var tol = new FakeTolerance();
        await Should.ThrowAsync<DomainException>(async () =>
            await RecordChangedCircumstanceHandler.Handle(
                new RecordChangedCircumstanceCommand("t1", "app-1",
                    "officer-1", ActorScope.Staff, "changed_info",
                    ["appraisal"]),
                new FakeApps(), tol, new FakeEventLog(), default));
    }

    [Fact]
    public async Task Non_staff_reset_forbidden()
    {
        using var _ = Scope();
        var tol = new FakeTolerance();
        await Should.ThrowAsync<DomainException>(async () =>
            await RecordChangedCircumstanceHandler.Handle(
                new RecordChangedCircumstanceCommand("t1", "app-1",
                    "borrower-1", ActorScope.Borrower, "changed_info",
                    ["appraisal"]),
                new FakeApps(), tol, new FakeEventLog(), default));
    }

    // -- RecordToleranceCure -------------------------------------------

    [Fact]
    public async Task Cure_closes_condition_and_writes_event()
    {
        using var _ = Scope();
        var conds = new FakeConditionRepository();
        var events = new FakeEventLog();
        var opened = await conds.OpenAsync(new ConditionDraft("app-1",
            ConditionKind.ToleranceCure, "violation", "detail",
            Blocking: true), default);

        var result = await RecordToleranceCureHandler.Handle(
            new RecordToleranceCureCommand("t1", "app-1", "officer-1",
                ActorScope.Staff, "refunded $50"),
            new FakeApps(), conds, events, default);

        result.ConditionId.ShouldBe(opened.ConditionId);
        conds.Store.ShouldHaveSingleItem().State
            .ShouldBe(ConditionState.Closed);
        events.Drafts.ShouldHaveSingleItem().EventKind
            .ShouldBe(LoanEventKind.ToleranceCure);
    }

    [Fact]
    public async Task Cure_without_open_violation_conflicts()
    {
        using var _ = Scope();
        await Should.ThrowAsync<DomainException>(async () =>
            await RecordToleranceCureHandler.Handle(
                new RecordToleranceCureCommand("t1", "app-1",
                    "officer-1", ActorScope.Staff, "refund"),
                new FakeApps(), new FakeConditionRepository(),
                new FakeEventLog(), default));
    }

    // -- GetApplicationTolerance ----------------------------------------

    [Fact]
    public async Task Report_returns_lineage_and_latest_diff()
    {
        using var _ = Scope();
        var tol = new FakeTolerance();
        var fees = new FakeFees();
        var conds = new FakeConditionRepository();
        await tol.AddVersionAsync("app-1", "le", "e1", "doc-1", null,
            default);
        await tol.AddVersionAsync("app-1", "le", "e2", "doc-2", null,
            default);
        fees.Items["e1"] = [Item("e1", "origination", "A", 900)];
        fees.Items["e2"] = [Item("e2", "origination", "A", 950)];
        fees.Evals["e2"] = new FeeEvalRecord("e2", "app-1", "stub",
            "h", "{}", "done", null, null, "a", null, null);
        tol.Classes["origination"] =
            new("origination", "zero", false);

        var report = await GetApplicationToleranceHandler.Handle(
            new GetApplicationToleranceQuery("t1", "app-1",
                "officer-1", ActorScope.Staff),
            new FakeApps(), tol, fees, conds, default);

        report.Versions.Count.ShouldBe(2);
        report.Violations.ShouldHaveSingleItem().Rule
            .ShouldBe("zero-tolerance");
        report.Buckets.ShouldHaveSingleItem().Bucket.ShouldBe("Zero");
        report.OpenCureConditionId.ShouldBeNull();
    }
}
