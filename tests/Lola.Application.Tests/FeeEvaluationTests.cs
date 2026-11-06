using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Fees;
using Lola.Application.Tenancy;
using Lola.Domain.Applications;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-183 handler-level proofs: enqueue writes eval+outbox and
/// audits `fee_eval`, a duplicate request resolves to the original
/// eval idempotently, borrowers can't evaluate fees, and the read
/// surfaces return normalized DTOs.
public sealed class FeeEvaluationTests
{
    private static ApplicationDocument Doc()
    {
        var d = ApplicationDocument.Empty();
        d.Set("loan_purpose", "purchase");
        d.Set("loan.product_type", "conventional");
        d.Set("loan.base_amount", "350000");
        d.Set("subject_property.state", "IL");
        d.Set("subject_property.zip", "62701");
        d.Set("declaration.va_eligible", "true");
        d.Set("party[0].first_name", "Test");
        d.Set("party[0].ssn", "000-00-0000");
        return d;
    }

    private static (
        FakeApps apps, FakeFeeRepo fees, FakeAudit audit,
        FakeAdapter adapter) Sut(ApplicationDocument? doc = null)
    {
        var apps = new FakeApps(doc ?? Doc());
        return (apps, new FakeFeeRepo(), new FakeAudit(),
            new FakeAdapter());
    }

    private static EvaluateApplicationFeesCommand Cmd(
        bool borrower = false) =>
        new("t1", "app-1", "officer-1", borrower, null);

    private static IDisposable Scope()
        => TenantContext.Use(new TenantScope("t1", "x.db", "k"));

    [Fact]
    public async Task Enqueue_writes_eval_outbox_and_audit()
    {
        var (apps, fees, audit, adapter) = Sut();
        using var _ = Scope();
        var result = await EvaluateApplicationFeesHandler.Handle(
            Cmd(), apps, fees, adapter, audit, default);

        result.Deduplicated.ShouldBeFalse();
        var eval = fees.Evals[result.EvalId];
        eval.Status.ShouldBe("pending");
        eval.Adapter.ShouldBe("fake");
        eval.ApplicationId.ShouldBe("app-1");
        eval.Actor.ShouldBe("officer-1");
        eval.ScenarioJson.ShouldContain("62701");
        eval.ScenarioJson.ShouldNotContain("000-00-0000");
        var outbox = fees.Outbox.Values.Single();
        outbox.Kind.ShouldBe("eval");
        outbox.EvalId.ShouldBe(result.EvalId);

        var evt = audit.Events.Single();
        evt.Action.ShouldBe("fee_eval");
        evt.EntityId.ShouldBe("app-1");
        evt.FieldClasses.ShouldContain("Ssn");
        evt.FieldClasses.ShouldContain("Name");
    }

    [Fact]
    public async Task Duplicate_request_returns_original_eval_no_audit()
    {
        var (apps, fees, audit, adapter) = Sut();
        using var _ = Scope();
        var first = await EvaluateApplicationFeesHandler.Handle(
            Cmd(), apps, fees, adapter, audit, default);
        var second = await EvaluateApplicationFeesHandler.Handle(
            Cmd(), apps, fees, adapter, audit, default);

        second.EvalId.ShouldBe(first.EvalId);
        second.Deduplicated.ShouldBeTrue();
        fees.Evals.Count.ShouldBe(1);
        fees.Outbox.Count.ShouldBe(1);
        audit.Events.Count.ShouldBe(1); // enqueued once, audited once
    }

    [Fact]
    public async Task Borrower_cannot_evaluate_fees()
    {
        var (apps, fees, audit, adapter) = Sut();
        using var _ = Scope();
        await Should.ThrowAsync<DomainException>(() =>
            EvaluateApplicationFeesHandler.Handle(Cmd(borrower: true),
                apps, fees, adapter, audit, default));
        fees.Evals.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reads_return_latest_and_history_dtos()
    {
        var (apps, fees, audit, adapter) = Sut();
        using var _ = Scope();
        var first = await EvaluateApplicationFeesHandler.Handle(
            Cmd(), apps, fees, adapter, audit, default);
        // A different request → a second eval (history grows).
        var second = await EvaluateApplicationFeesHandler.Handle(
            Cmd() with { FeeCodes = ["origination"] }, apps, fees,
            adapter, audit, default);
        second.EvalId.ShouldNotBe(first.EvalId);

        var latest = await GetApplicationFeesHandler.Handle(
            new GetApplicationFeesQuery("t1", "app-1", "officer-1", false),
            apps, fees, default);
        latest.ShouldNotBeNull();
        latest!.EvalId.ShouldBe(second.EvalId);
        latest.Status.ShouldBe("pending");
        latest.Adapter.ShouldBe("fake");

        var history = await GetFeeHistoryHandler.Handle(
            new GetFeeHistoryQuery("t1", "app-1", "officer-1", false),
            apps, fees, default);
        history.Count.ShouldBe(2);
        history[0].EvalId.ShouldBe(first.EvalId);
        history[1].EvalId.ShouldBe(second.EvalId);
    }

    [Fact]
    public async Task Unknown_application_404s()
    {
        var (apps, fees, audit, adapter) = Sut();
        using var _ = Scope();
        await Should.ThrowAsync<DomainException>(() =>
            EvaluateApplicationFeesHandler.Handle(
                Cmd() with { AppId = "nope" }, apps, fees, adapter,
                audit, default));
    }

    // ---- MPE-184 override tests ------------------------------------

    private static void SeedItem(FakeFeeRepo fees, string evalId,
        string itemId, string feeCode, string section)
    {
        fees.Evals[evalId] = new FeeEvalRecord(evalId, "app-1",
            "fake", "h", "{}", "done", null, null, "o", null, null);
        fees.Items.Add(new FeeItemRecord(itemId, evalId, feeCode,
            null, section, 100.0, "[]", 100.0, null));
    }

    private static OverrideFeeCommand OverrideCmd(
        string itemId = "item-1", decimal? amount = 500m,
        string? reason = "LO corrections") =>
        new("t1", "app-1", itemId, "officer-1", false, amount, reason);

    [Fact]
    public async Task Set_override_persists_and_audits()
    {
        var (apps, fees, audit, adapter) = Sut();
        SeedItem(fees, "eval-1", "item-1", "origination", "A");
        using var _ = Scope();
        var r = await OverrideFeeHandler.Handle(
            OverrideCmd(), apps, fees, audit, default);

        r.Cleared.ShouldBeFalse();
        var o = fees.Overrides.Values.Single();
        o.FeeCode.ShouldBe("origination");
        o.Section.ShouldBe("A");
        o.Amount.ShouldBe(500.0);
        o.Reason.ShouldBe("LO corrections");
        audit.Events.Single().Action.ShouldBe("fee_override");
    }

    [Fact]
    public async Task Override_requires_reason()
    {
        var (apps, fees, audit, adapter) = Sut();
        SeedItem(fees, "eval-1", "item-1", "origination", "A");
        using var _ = Scope();
        await Should.ThrowAsync<DomainException>(() =>
            OverrideFeeHandler.Handle(
                OverrideCmd(reason: null), apps, fees, audit, default));
        fees.Overrides.ShouldBeEmpty();
    }

    [Fact]
    public async Task Clear_restores_computed_and_audits()
    {
        var (apps, fees, audit, adapter) = Sut();
        SeedItem(fees, "eval-1", "item-1", "origination", "A");
        using var _ = Scope();
        var set = await OverrideFeeHandler.Handle(
            OverrideCmd(), apps, fees, audit, default);
        var cleared = await OverrideFeeHandler.Handle(
            OverrideCmd(amount: null, reason: "client declined"),
            apps, fees, audit, default);

        cleared.Cleared.ShouldBeTrue();
        cleared.OverrideId.ShouldBe(set.OverrideId);
        (await fees.ListOverridesAsync("app-1", default))
            .ShouldBeEmpty(); // cleared rows stop affecting totals
        audit.Events.Select(e => e.Action).ShouldBe(
            ["fee_override", "fee_override_clear"]);
    }

    [Fact]
    public async Task Borrower_cannot_override()
    {
        var (apps, fees, audit, adapter) = Sut();
        using var _ = Scope();
        await Should.ThrowAsync<DomainException>(() =>
            OverrideFeeHandler.Handle(
                OverrideCmd() with { ActorIsBorrower = true },
                apps, fees, audit, default));
    }

    [Fact]
    public async Task Aggregator_sections_override_stale_and_effective()
    {
        var eval = new FeeEvalRecord("e1", "app-1", "z", "h", "{}",
            "done", null, null, "o", null, null);
        var items = new List<FeeItemRecord>
        {
            new("i1", "e1", "origination", "Orig", "A", 900.0, "[]",
                900.0, null),
            new("i2", "e1", "appraisal", "Apr", "C", 650.0, "[]",
                650.0, null),
        };
        var overrides = new List<FeeOverrideRecord>
        {
            // live override wins on origination
            new("o1", "app-1", "origination", "A", 700.0, "neg", "off",
                null, 0, null),
            // stale — fee vanished from the eval
            new("o2", "app-1", "pest-inspection", "C", 75.0, "req",
                "off", null, 1, null),
            // cleared — affects nothing
            new("o3", "app-1", "appraisal", "C", 1.0, "old", "off",
                null, 0, "2026-01-01T00:00:00Z"),
        };
        var b = FeeAggregator.ToBreakdown(eval, items, overrides);

        b.Sections.Count.ShouldBe(2);
        var a = b.Sections.Single(s => s.Section == "A");
        a.Items[0].OverrideAmount.ShouldBe(700m);
        a.Items[0].Effective.ShouldBe(700m);
        a.EffectiveTotal.ShouldBe(700m);
        b.EffectiveTotal.ShouldBe(1350m);
        b.ComputedTotal.ShouldBe(1550m);
        b.StaleOverrides.Single().FeeCode.ShouldBe("pest-inspection");
    }

    // ---- fakes -----------------------------------------------------

    private sealed class FakeApps(ApplicationDocument doc)
        : IApplicationRepository
    {
        public Task<ApplicationRecord?> GetAsync(string id,
            CancellationToken ct = default)
            => Task.FromResult<ApplicationRecord?>(id == "app-1"
                ? new ApplicationRecord(id, "draft", "POSIntake",
                    null, 1, null, null, doc)
                : null);
        public Task InsertAsync(string id, ApplicationDocument d, string s, string? b, string w, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> WriteVersionAsync(string id, ApplicationDocument d, string s, string w, bool submitted, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateStatusAsync(string id, string s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EnsureTridClockStartAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdatePipelineStepAsync(string id, string s, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordStepEventAsync(string id, string s, string e, string a, string? r, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationRetentionRow>> RetentionWindowsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationMeta>> ListAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeFeeRepo : IFeeRepository
    {
        public Dictionary<string, FeeEvalRecord> Evals { get; } = new();
        public Dictionary<string, FeeOutboxRow> Outbox { get; } = new();
        private readonly Dictionary<string, string> _byHash = new();

        public Task<FeeEnqueueResult> EnqueueAsync(FeeEvalRecord eval,
            string outboxId, string kind, string payloadJson,
            CancellationToken ct = default)
        {
            if (_byHash.TryGetValue(eval.RequestHash, out var existing))
                return Task.FromResult(
                    new FeeEnqueueResult(existing, true));
            eval = eval with
            {
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            Evals[eval.EvalId] = eval;
            Outbox[outboxId] = new FeeOutboxRow(outboxId, kind,
                eval.EvalId, eval.RequestHash, payloadJson, "pending",
                0, null, null);
            _byHash[eval.RequestHash] = eval.EvalId;
            return Task.FromResult(
                new FeeEnqueueResult(eval.EvalId, false));
        }

        public Task<FeeEvalRecord?> GetEvalAsync(string evalId,
            CancellationToken ct = default)
            => Task.FromResult(Evals.GetValueOrDefault(evalId));

        public Task<FeeEvalRecord?> GetLatestEvalAsync(string appId,
            CancellationToken ct = default)
            => Task.FromResult(Evals.Values
                .Where(e => e.ApplicationId == appId)
                .OrderByDescending(e => e.CreatedUtc ?? e.EvalId)
                .FirstOrDefault());

        public Task<IReadOnlyList<FeeEvalRecord>> ListEvalsAsync(
            string appId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeEvalRecord>>(
                Evals.Values.Where(e => e.ApplicationId == appId)
                    .OrderBy(e => e.CreatedUtc ?? e.EvalId).ToList());

        public Task<IReadOnlyList<FeeOutboxRow>> ListDueOutboxAsync(
            string nowUtc, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeOutboxRow>>(
                Outbox.Values.Where(r => r.Status == "pending" &&
                    (r.NextRetryUtc is null ||
                     string.CompareOrdinal(r.NextRetryUtc, nowUtc) <= 0))
                    .Take(limit).ToList());

        public Task CompleteAsync(string outboxId, string evalId,
            string adapter, string resultJson,
            IReadOnlyList<FeeItemRecord> items, string completedUtc,
            CancellationToken ct = default)
        {
            var o = Outbox[outboxId];
            Outbox[outboxId] = o with { Status = "done" };
            var e = Evals[evalId];
            Evals[evalId] = e with
            {
                Status = "done",
                Adapter = adapter,
                ResultJson = resultJson,
                CompletedUtc = completedUtc
            };
            return Task.CompletedTask;
        }

        public Task RescheduleAsync(string outboxId, string evalId,
            long attempts, string nextRetryUtc, string error,
            CancellationToken ct = default)
        {
            var o = Outbox[outboxId];
            Outbox[outboxId] = o with
            {
                Attempts = attempts,
                NextRetryUtc = nextRetryUtc,
                LastError = error
            };
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(string outboxId, string evalId,
            long attempts, string error, CancellationToken ct = default)
        {
            var o = Outbox[outboxId];
            Outbox[outboxId] = o with
            {
                Status = "dlq",
                Attempts = attempts,
                LastError = error
            };
            var e = Evals[evalId];
            Evals[evalId] = e with { Status = "failed", Error = error };
            return Task.CompletedTask;
        }

        public List<FeeItemRecord> Items { get; } = [];
        public Dictionary<string, FeeOverrideRecord> Overrides { get; } = new();

        public Task<FeeItemLookup?> GetItemAsync(string itemId,
            CancellationToken ct = default)
        {
            var item = Items.FirstOrDefault(i => i.ItemId == itemId);
            if (item is null)
                return Task.FromResult<FeeItemLookup?>(null);
            var appId = Evals.TryGetValue(item.EvalId, out var e)
                ? e.ApplicationId : "";
            return Task.FromResult<FeeItemLookup?>(new(item, appId));
        }

        public Task<IReadOnlyList<FeeItemRecord>> ListItemsAsync(
            string evalId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeItemRecord>>(
                Items.Where(i => i.EvalId == evalId).ToList());

        public Task<FeeOverrideRecord?> GetOverrideAsync(
            string overrideId, CancellationToken ct = default)
            => Task.FromResult(Overrides.GetValueOrDefault(overrideId));

        public Task<FeeOverrideRecord> UpsertOverrideAsync(
            FeeOverrideRecord o, CancellationToken ct = default)
        {
            var existing = Overrides.Values.FirstOrDefault(x =>
                x.ApplicationId == o.ApplicationId &&
                x.FeeCode == o.FeeCode && x.Section == o.Section &&
                x.ClearedUtc is null);
            var merged = existing is null ? o
                : o with { OverrideId = existing.OverrideId };
            Overrides[merged.OverrideId] = merged;
            return Task.FromResult(merged);
        }

        public Task ClearOverrideAsync(string overrideId,
            string clearedUtc, CancellationToken ct = default)
        {
            Overrides[overrideId] = Overrides[overrideId] with
            { ClearedUtc = clearedUtc };
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<FeeOverrideRecord>> ListOverridesAsync(
            string appId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FeeOverrideRecord>>(
                Overrides.Values.Where(o => o.ApplicationId == appId &&
                    o.ClearedUtc is null).ToList());
    }

    private sealed class FakeAudit : ITenantAuditLog
    {
        public List<(string Actor, string Action, string Entity,
            string? EntityId, IReadOnlyList<string> FieldClasses,
            string? Reason)> Events
        { get; } = [];

        public Task WriteAsync(string actor, string action, string entity,
            string? entityId, IReadOnlyList<string> fieldClasses,
            string? reason, CancellationToken ct = default)
        {
            Events.Add((actor, action, entity, entityId, fieldClasses,
                reason));
            return Task.CompletedTask;
        }
        public Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditFilter f, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeAdapter : IFeeService
    {
        public string Adapter => "fake";
        public Task<FeeEvaluation> EvaluateAsync(
            FeeScenario s, FeeRequestOptions o,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
