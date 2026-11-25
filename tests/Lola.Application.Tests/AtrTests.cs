using Lola.Application.Applications;
using Lola.Application.Compliance;
using Lola.Application.Errors;
using Lola.Application.Events;
using Lola.Application.Fees;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Domain.Applications;
using Shouldly;
using Xunit;

namespace Lola.Application.Tests;

/// M22 P2 (MPE-307) — ATR/QM evaluation wiring: doc→input mapping,
/// result + event writes, report shape, and the negative-case matrix.
public sealed class AtrTests
{
    // -- fakes ------------------------------------------------------

    private sealed class FakeAtr : IAtrQmRepository
    {
        public List<AtrResultRecord> Results { get; } = [];
        public List<AporRateRecord> Apors { get; } =
            [new("2025-01-01", 6.50)];
        public Dictionary<string, PfClassRecord> PfClasses { get; }
            = new();
        public Dictionary<string, StateHighCostRecord> StateRules
        { get; } = new();

        public Task<AtrResultRecord> AddResultAsync(string appId,
            string? evalId, decimal? dti, decimal? apr, decimal? apor,
            string verdictsJson, string actor, CancellationToken ct)
        {
            var r = new AtrResultRecord($"atr-{Results.Count + 1}",
                appId, evalId, (double?)dti, (double?)apr, (double?)apor,
                verdictsJson, actor,
                "2026-01-01T00:00:00.000Z");
            Results.Add(r);
            return Task.FromResult(r);
        }

        public Task<IReadOnlyList<AtrResultRecord>> ListResultsAsync(
            string appId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AtrResultRecord>>(
                Results.Where(r => r.ApplicationId == appId)
                    .OrderByDescending(r => r.CreatedUtc).ToList());

        public Task<AporRateRecord?> GetAporAsync(CancellationToken ct)
            => Task.FromResult(Apors
                .OrderByDescending(a => a.EffectiveDate)
                .FirstOrDefault());

        public Task<IReadOnlyDictionary<string, PfClassRecord>>
            GetPfClassesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string,
                PfClassRecord>>(PfClasses);

        public Task<StateHighCostRecord?> GetStateRuleAsync(
            string stateCode, CancellationToken ct)
            => Task.FromResult(StateRules.TryGetValue(
                stateCode.ToUpperInvariant(), out var r) ? r : null);
    }

    private sealed class FakeFees : IFeeRepository
    {
        public Dictionary<string, List<FeeItemRecord>> Items { get; }
            = new();
        public List<FeeOverrideRecord> Overrides { get; } = [];
        public List<FeeEvalRecord> Evals { get; } = [];

        public Task<IReadOnlyList<FeeItemRecord>> ListItemsAsync(
            string evalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeItemRecord>>(
                Items.GetValueOrDefault(evalId) ?? []);
        public Task<IReadOnlyList<FeeEvalRecord>> ListEvalsAsync(
            string appId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeEvalRecord>>(Evals);
        public Task<IReadOnlyList<FeeOverrideRecord>>
            ListOverridesAsync(string appId,
                CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeOverrideRecord>>(
                Overrides);
        public Task<FeeItemLookup?> GetItemAsync(string itemId,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<FeeEvalRecord?> GetEvalAsync(string evalId,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<FeeEvalRecord?> GetLatestEvalAsync(string appId,
            CancellationToken ct = default)
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
        public ApplicationDocument Doc = ApplicationDocument.Empty();
        public Task<ApplicationRecord?> GetAsync(string id,
            CancellationToken ct = default)
            => Task.FromResult<ApplicationRecord?>(new(id, "draft",
                "intake", null, 1, null, null, Doc));
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

    /// A doc carrying a clean conforming loan — $300k / 6.5% / 360 /
    /// $8k income / $500 debts — in VA (no seeded state rule).
    private static ApplicationDocument CleanDoc()
    {
        var d = ApplicationDocument.Empty();
        d.Set("loan.base_amount", "300000");
        d.Set("interest_rate", "6.5");
        d.Set("loan_term_months", "360");
        d.Set("amortization_type", "fixed");
        d.Set("loan.lien_position", "first");
        d.Set("loan.product_type", "conventional");
        d.Set("subject_property.state", "VA");
        d.Set("party[0].employment[0].monthly_income", "8000");
        d.Set("liability[0].monthly_payment", "500");
        return d;
    }

    private static FeeEvalRecord DoneEval(string evalId)
        => new(evalId, "app-1", "zara", "hash-1", "{}", "done",
            null, null, "officer-1", "2026-01-01T00:00:00.000Z",
            "2026-01-01T00:01:00.000Z", "batch-1", "");

    private static FeeItemRecord Item(string evalId, string code,
        double total)
        => new($"it-{evalId}-{code}", evalId, code, code, "A",
            total, "[]", total, "2026-01-01T00:00:00.000Z");

    private static AtrVerdictDto Verdict(AtrEvaluationDto eval,
        string test)
        => eval.Verdicts.Single(v => v.Test == test);

    // -- EvaluateAtrQm -----------------------------------------------

    [Fact]
    public async Task Evaluate_writes_result_row_and_event()
    {
        using var _ = Scope();
        var atr = new FakeAtr();
        var fees = new FakeFees();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = CleanDoc() };
        fees.Evals.Add(DoneEval("e1"));
        fees.Items["e1"] = [Item("e1", "origination", 1000)];
        atr.PfClasses["origination"] = new("origination", "counted", null);

        var result = await EvaluateAtrQmHandler.Handle(
            new EvaluateAtrQmCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, fees, events, default);

        atr.Results.ShouldHaveSingleItem().EvalId.ShouldBe("e1");
        var draft = events.Drafts.ShouldHaveSingleItem();
        draft.EventKind.ShouldBe(LoanEventKind.AtrQmResult);
        draft.Actor.ShouldBe("officer-1");
        draft.AfterVal!.ShouldContain("qm_general:");
        result.Verdicts.Count.ShouldBe(6);
        result.Dti.ShouldNotBeNull();
        result.Apr.ShouldNotBeNull();
        result.Apor.ShouldBe(6.50m);
    }

    [Fact]
    public async Task Get_report_returns_battery_and_lineage()
    {
        using var _ = Scope();
        var atr = new FakeAtr();
        var fees = new FakeFees();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = CleanDoc() };
        fees.Evals.Add(DoneEval("e1"));
        fees.Items["e1"] = [Item("e1", "origination", 1000)];
        atr.PfClasses["origination"] = new("origination", "counted", null);

        await EvaluateAtrQmHandler.Handle(new EvaluateAtrQmCommand(
            "t1", "app-1", "officer-1", ActorScope.Staff),
            apps, atr, fees, events, default);

        var report = await GetApplicationAtrHandler.Handle(
            new GetApplicationAtrQuery("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, default);

        report.Latest.ShouldNotBeNull();
        report.History.ShouldHaveSingleItem();
        report.Latest.Verdicts.Select(v => v.Test).ShouldBe(
            ["atr_general", "qm_general", "qm_hud", "hoepa", "hpml",
             "state_high_cost"], ignoreOrder: true);
        Verdict(report.Latest, "qm_general").Verdict.ShouldBe("pass");
        Verdict(report.Latest, "atr_general").Detail
            .ShouldContain("dti=29.95");
    }

    [Fact]
    public async Task No_completed_eval_warns_pf_tests_not_pass()
    {
        using var _ = Scope();
        var atr = new FakeAtr();
        var fees = new FakeFees();   // no evals at all
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = CleanDoc() };

        var result = await EvaluateAtrQmHandler.Handle(
            new EvaluateAtrQmCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, fees, events, default);

        Verdict(result, "qm_general").Verdict.ShouldBe("warn");
        Verdict(result, "hoepa").Verdict.ShouldBe("warn");
        // …but the row + event still landed.
        atr.Results.ShouldHaveSingleItem().EvalId.ShouldBeNull();
        events.Drafts.ShouldHaveSingleItem().EventKind
            .ShouldBe(LoanEventKind.AtrQmResult);
    }

    [Fact]
    public async Task Missing_doc_inputs_warn_never_throw()
    {
        using var _ = Scope();
        var atr = new FakeAtr();
        var fees = new FakeFees();
        var events = new FakeEventLog();
        // Empty doc — no income, no term, no amount, no rate.
        var apps = new FakeApps { Doc = ApplicationDocument.Empty() };

        var result = await EvaluateAtrQmHandler.Handle(
            new EvaluateAtrQmCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, fees, events, default);

        Verdict(result, "atr_general").Verdict.ShouldBe("warn");
        Verdict(result, "qm_general").Verdict.ShouldBe("warn");
        result.Dti.ShouldBeNull();
        result.Apr.ShouldBeNull();
    }

    [Fact]
    public async Task Two_evaluates_append_two_rows_two_events()
    {
        using var _ = Scope();
        var atr = new FakeAtr();
        var fees = new FakeFees();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = CleanDoc() };

        var cmd = new EvaluateAtrQmCommand("t1", "app-1", "officer-1",
            ActorScope.Staff);
        await EvaluateAtrQmHandler.Handle(cmd, apps, atr, fees, events,
            default);
        await EvaluateAtrQmHandler.Handle(cmd, apps, atr, fees, events,
            default);

        atr.Results.Count.ShouldBe(2);
        events.Drafts.Count(d =>
            d.EventKind == LoanEventKind.AtrQmResult).ShouldBe(2);
        atr.Results[0].ResultId.ShouldNotBe(atr.Results[1].ResultId);
    }

    [Fact]
    public async Task Empty_apor_table_warns_spread_tests()
    {
        using var _ = Scope();
        var atr = new FakeAtr();
        atr.Apors.Clear();
        var fees = new FakeFees();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = CleanDoc() };
        fees.Evals.Add(DoneEval("e1"));
        fees.Items["e1"] = [Item("e1", "origination", 1000)];

        var result = await EvaluateAtrQmHandler.Handle(
            new EvaluateAtrQmCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, fees, events, default);

        Verdict(result, "hoepa").Verdict.ShouldBe("warn");
        Verdict(result, "hpml").Verdict.ShouldBe("warn");
        result.Apor.ShouldBeNull();
    }

    [Fact]
    public async Task Non_staff_actor_forbidden()
    {
        using var _ = Scope();
        await Should.ThrowAsync<DomainException>(async () =>
            await EvaluateAtrQmHandler.Handle(
                new EvaluateAtrQmCommand("t1", "app-1", "borrower-1",
                    ActorScope.Borrower),
                new FakeApps(), new FakeAtr(), new FakeFees(),
                new FakeEventLog(), default));
    }

    [Fact]
    public async Task Unknown_fee_code_counts_toward_pf()
    {
        using var _ = Scope();
        var atr = new FakeAtr();   // pf_classes EMPTY — fail closed
        var fees = new FakeFees();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = CleanDoc() };
        fees.Evals.Add(DoneEval("e1"));
        // $9,001 of unregistered fees on $300k → over the 3% cap.
        fees.Items["e1"] = [Item("e1", "never-seen", 9001)];

        var result = await EvaluateAtrQmHandler.Handle(
            new EvaluateAtrQmCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, fees, events, default);

        Verdict(result, "qm_general").Verdict.ShouldBe("fail");
        Verdict(result, "qm_general").Detail.ShouldContain("p&f");
    }

    [Fact]
    public async Task State_rule_row_applies_to_matching_state()
    {
        using var _ = Scope();
        var atr = new FakeAtr();
        atr.StateRules["NY"] = new("NY", 6.5, 5.0, 50_000, null);
        var fees = new FakeFees();
        var events = new FakeEventLog();
        var doc = CleanDoc();
        doc.Set("subject_property.state", "NY");
        var apps = new FakeApps { Doc = doc };
        fees.Evals.Add(DoneEval("e1"));
        fees.Items["e1"] = [Item("e1", "origination", 1000)];

        var result = await EvaluateAtrQmHandler.Handle(
            new EvaluateAtrQmCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, fees, events, default);

        var v = Verdict(result, "state_high_cost");
        v.Verdict.ShouldBe("pass");
        v.Detail.ShouldContain("NY");
    }

    [Fact]
    public async Task Override_amount_wins_in_pf_aggregate()
    {
        using var _ = Scope();
        var atr = new FakeAtr();
        var fees = new FakeFees();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = CleanDoc() };
        fees.Evals.Add(DoneEval("e1"));
        fees.Items["e1"] = [Item("e1", "origination", 9500)];
        atr.PfClasses["origination"] = new("origination", "counted", null);
        // Officer overrides the fee down to $1,000 — inside the cap.
        fees.Overrides.Add(new FeeOverrideRecord("ov-1", "app-1",
            "origination", "A", 1000, "cure", "officer-1",
            "2026-01-01T00:00:00.000Z", 0, null, ""));

        var result = await EvaluateAtrQmHandler.Handle(
            new EvaluateAtrQmCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, fees, events, default);

        Verdict(result, "qm_general").Verdict.ShouldBe("pass");
    }

    [Fact]
    public async Task Event_summary_carries_verdicts_not_field_values()
    {
        using var _ = Scope();
        var atr = new FakeAtr();
        var fees = new FakeFees();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = CleanDoc() };

        await EvaluateAtrQmHandler.Handle(
            new EvaluateAtrQmCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, fees, events, default);

        var after = events.Drafts.ShouldHaveSingleItem().AfterVal!;
        after.ShouldContain("atr_general:");
        after.ShouldNotContain("8000");   // no raw income figure
        after.ShouldNotContain("300000"); // no raw loan amount
    }
}
