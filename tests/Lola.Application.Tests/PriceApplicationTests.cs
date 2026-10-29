using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Pricing;
using Lola.Application.Tenancy;
using Lola.Domain.Applications;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-130 handler-level proofs: enqueue writes eval+outbox and audits
/// `price_eval`, a duplicate request resolves to the original eval
/// idempotently, borrowers can't price, and the read surfaces return
/// normalized DTOs.
public sealed class PriceApplicationTests
{
    private static ApplicationDocument Doc() => DocWith(new()
    {
        ["loan_purpose"] = "purchase",
        ["loan.product_type"] = "conventional",
        ["loan.base_amount"] = "350000",
        ["subject_property.state"] = "IL",
        ["party[0].credit_score"] = "720",
        ["party[0].first_name"] = "Test",
        ["party[0].ssn"] = "000-00-0000",
    });

    private static ApplicationDocument DocWith(Dictionary<string, string> fields)
    {
        var d = ApplicationDocument.Empty();
        foreach (var (k, v) in fields) d.Set(k, v);
        return d;
    }

    private static (
        FakeApps apps, FakePricingRepo pricing, FakeAudit audit,
        FakeAdapter adapter) Sut(ApplicationDocument? doc = null)
    {
        var apps = new FakeApps(doc ?? Doc());
        return (apps, new FakePricingRepo(), new FakeAudit(),
            new FakeAdapter());
    }

    private static PriceApplicationCommand Cmd(bool borrower = false) =>
        new("t1", "app-1", "officer-1", borrower,
            null, null, 30, null, false);

    private static IDisposable Scope()
        => TenantContext.Use(new TenantScope("t1", "x.db", "k"));

    [Fact]
    public async Task Enqueue_writes_eval_outbox_and_audit()
    {
        var (apps, pricing, audit, adapter) = Sut();
        using var _ = Scope();
        var result = await PriceApplicationHandler.Handle(
            Cmd(), apps, pricing, adapter, audit, default);

        result.Deduplicated.ShouldBeFalse();
        var eval = pricing.Evals[result.EvalId];
        eval.Status.ShouldBe("pending");
        eval.Adapter.ShouldBe("fake");
        eval.ApplicationId.ShouldBe("app-1");
        eval.Actor.ShouldBe("officer-1");
        var outbox = pricing.Outbox.Values.Single();
        outbox.Kind.ShouldBe("eval");
        outbox.EvalId.ShouldBe(result.EvalId);

        var evt = audit.Events.Single();
        evt.Action.ShouldBe("price_eval");
        evt.EntityId.ShouldBe("app-1");
        evt.FieldClasses.ShouldContain("Ssn");
        evt.FieldClasses.ShouldContain("Name");
    }

    [Fact]
    public async Task Duplicate_request_returns_original_eval_no_audit()
    {
        var (apps, pricing, audit, adapter) = Sut();
        using var _ = Scope();
        var first = await PriceApplicationHandler.Handle(
            Cmd(), apps, pricing, adapter, audit, default);
        var second = await PriceApplicationHandler.Handle(
            Cmd(), apps, pricing, adapter, audit, default);

        second.EvalId.ShouldBe(first.EvalId);
        second.Deduplicated.ShouldBeTrue();
        pricing.Evals.Count.ShouldBe(1);
        pricing.Outbox.Count.ShouldBe(1);
        audit.Events.Count.ShouldBe(1); // enqueued once, audited once
    }

    [Fact]
    public async Task Borrower_cannot_price()
    {
        var (apps, pricing, audit, adapter) = Sut();
        using var _ = Scope();
        await Should.ThrowAsync<DomainException>(() =>
            PriceApplicationHandler.Handle(Cmd(borrower: true),
                apps, pricing, adapter, audit, default));
        pricing.Evals.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reads_return_latest_and_history_dtos()
    {
        var (apps, pricing, audit, adapter) = Sut();
        using var _ = Scope();
        var first = await PriceApplicationHandler.Handle(
            Cmd(), apps, pricing, adapter, audit, default);
        // A different request → a second eval (history grows).
        var second = await PriceApplicationHandler.Handle(
            Cmd() with { LockTermDays = 45 }, apps, pricing, adapter,
            audit, default);
        second.EvalId.ShouldNotBe(first.EvalId);

        var latest = await GetApplicationPricingHandler.Handle(
            new GetApplicationPricingQuery("t1", "app-1", "officer-1", false),
            apps, pricing, default);
        latest.ShouldNotBeNull();
        latest!.EvalId.ShouldBe(second.EvalId);
        latest.Status.ShouldBe("pending");
        latest.Adapter.ShouldBe("fake");

        var history = await GetPricingHistoryHandler.Handle(
            new GetPricingHistoryQuery("t1", "app-1", "officer-1", false),
            apps, pricing, default);
        history.Count.ShouldBe(2);
        history[0].EvalId.ShouldBe(first.EvalId);
        history[1].EvalId.ShouldBe(second.EvalId);
    }

    [Fact]
    public async Task Unknown_application_404s()
    {
        var (apps, pricing, audit, adapter) = Sut();
        using var _ = Scope();
        await Should.ThrowAsync<DomainException>(() =>
            PriceApplicationHandler.Handle(
                Cmd() with { AppId = "nope" }, apps, pricing, adapter,
                audit, default));
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

    private sealed class FakePricingRepo : IPricingRepository
    {
        public Dictionary<string, PricingEvalRecord> Evals { get; } = new();
        public Dictionary<string, PricingOutboxRow> Outbox { get; } = new();
        private readonly Dictionary<string, string> _byHash = new();

        public Task<EnqueueResult> EnqueueAsync(PricingEvalRecord eval,
            string outboxId, string kind, string payloadJson,
            CancellationToken ct = default)
        {
            if (_byHash.TryGetValue(eval.RequestHash, out var existing))
                return Task.FromResult(new EnqueueResult(existing, true));
            eval = eval with
            {
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            Evals[eval.EvalId] = eval;
            Outbox[outboxId] = new PricingOutboxRow(outboxId, kind,
                eval.EvalId, eval.RequestHash, payloadJson, "pending",
                0, null, null);
            _byHash[eval.RequestHash] = eval.EvalId;
            return Task.FromResult(new EnqueueResult(eval.EvalId, false));
        }

        public Task<PricingEvalRecord?> GetEvalAsync(string evalId,
            CancellationToken ct = default)
            => Task.FromResult(Evals.GetValueOrDefault(evalId));

        public Task<PricingEvalRecord?> GetLatestEvalAsync(string appId,
            CancellationToken ct = default)
            => Task.FromResult(Evals.Values
                .Where(e => e.ApplicationId == appId)
                .OrderByDescending(e => e.CreatedUtc ?? e.EvalId)
                .FirstOrDefault());

        public Task<IReadOnlyList<PricingEvalRecord>> ListEvalsAsync(
            string appId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PricingEvalRecord>>(
                Evals.Values.Where(e => e.ApplicationId == appId)
                    .OrderBy(e => e.CreatedUtc ?? e.EvalId).ToList());

        public Task<IReadOnlyList<PricingOutboxRow>> ListDueOutboxAsync(
            string nowUtc, int limit, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PricingOutboxRow>>(
                Outbox.Values.Where(r => r.Status == "pending" &&
                    (r.NextRetryUtc is null ||
                     string.CompareOrdinal(r.NextRetryUtc, nowUtc) <= 0))
                    .Take(limit).ToList());

        public Task CompleteAsync(string outboxId, string evalId,
            string adapter, string resultJson, string completedUtc,
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

    private sealed class FakeAdapter : IPricingService
    {
        public string Adapter => "fake";
        public Task<PricingEvaluation> EvaluateAsync(
            PricingScenario s, PricingRequestOptions o,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
