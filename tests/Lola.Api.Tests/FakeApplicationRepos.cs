using System.Collections.Concurrent;
using Lola.Application.Applications;
using Lola.Application.Pricing;
using Lola.Domain.Applications;

namespace Lola.Api.Tests;

/// Shared in-memory store so the fake app + party repos behave like the
/// real seam — search simulates a blind lookup by matching leaf values.
public sealed class FakeApplicationStore
{
    public sealed class App
    {
        public string Status = "draft";
        public string PipelineStep = "POSIntake";
        public string? BorrowerUserId;
        public int Version;
        public string? TridClockStart;
        public string? SubmittedUtc;
        public string CreatedUtc = DateTimeOffset.UtcNow.ToString("O");
        public ApplicationDocument Doc = ApplicationDocument.Empty();
    }

    public ConcurrentDictionary<string, App> Apps { get; } = new();
    public ConcurrentDictionary<string, List<StepEventDto>> Steps { get; } = new();
}

public sealed class FakeApplicationRepository(FakeApplicationStore store)
    : IApplicationRepository
{
    public Task InsertAsync(string id, ApplicationDocument doc, string status,
        string? borrowerUserId, string writtenBy, CancellationToken ct = default)
    {
        store.Apps[id] = new FakeApplicationStore.App
        {
            Status = status,
            BorrowerUserId = borrowerUserId,
            Version = 1,
            Doc = doc
        };
        return Task.CompletedTask;
    }

    public Task<int> WriteVersionAsync(string id, ApplicationDocument doc,
        string status, string writtenBy, bool submitted, CancellationToken ct = default)
    {
        var app = store.Apps[id];
        app.Version++;
        app.Status = status;
        app.Doc = doc;
        if (submitted) app.SubmittedUtc ??= DateTimeOffset.UtcNow.ToString("O");
        return Task.FromResult(app.Version);
    }

    public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default)
    {
        store.Apps[id].Status = status;
        return Task.CompletedTask;
    }

    public Task EnsureTridClockStartAsync(string id, CancellationToken ct = default)
    {
        store.Apps[id].TridClockStart ??= DateTimeOffset.UtcNow.ToString("O");
        return Task.CompletedTask;
    }

    public Task UpdatePipelineStepAsync(string id, string step,
        CancellationToken ct = default)
    {
        store.Apps[id].PipelineStep = step;
        return Task.CompletedTask;
    }

    public Task RecordStepEventAsync(string id, string step, string event_,
        string actor, string? reason, CancellationToken ct = default)
    {
        var list = store.Steps.GetOrAdd(id, _ => []);
        lock (list)
            list.Add(new StepEventDto($"{id}-step-{list.Count + 1}", step,
                event_, actor, reason, DateTimeOffset.UtcNow.ToString("O")));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
        string id, CancellationToken ct = default)
    {
        if (!store.Steps.TryGetValue(id, out var list))
            return Task.FromResult<IReadOnlyList<StepEventDto>>([]);
        lock (list) return Task.FromResult<IReadOnlyList<StepEventDto>>(list.ToList());
    }

    public Task<ApplicationRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        if (!store.Apps.TryGetValue(id, out var app)) return Task.FromResult<ApplicationRecord?>(null);
        // Clone — handlers mutate the doc (DocMasker) per request.
        return Task.FromResult<ApplicationRecord?>(new ApplicationRecord(
            id, app.Status, app.PipelineStep, app.BorrowerUserId, app.Version,
            app.TridClockStart, app.SubmittedUtc,
            ApplicationDocument.FromObject(app.Doc.CloneRoot())));
    }

    public Task<IReadOnlyList<ApplicationRetentionRow>> RetentionWindowsAsync(
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApplicationRetentionRow>>(store.Apps
            .Select(kv => new ApplicationRetentionRow(kv.Key, kv.Value.CreatedUtc))
            .ToList());

    public Task<IReadOnlyList<ApplicationMeta>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApplicationMeta>>(store.Apps
            .Select(kv => new ApplicationMeta(kv.Key, kv.Value.Status,
                kv.Value.PipelineStep, kv.Value.BorrowerUserId,
                kv.Value.Version, kv.Value.TridClockStart))
            .ToList());
}

public sealed class FakeApplicationPartyRepository(FakeApplicationStore store)
    : IApplicationPartyRepository
{
    public Task<IReadOnlyList<string>> SearchAsync(string field, string value,
        CancellationToken ct = default)
    {
        var suffix = $".{field}";
        return Task.FromResult<IReadOnlyList<string>>(store.Apps
            .Where(kv => kv.Value.Doc.Leaves().Any(l =>
                l.Path == field || l.Path.EndsWith(suffix, StringComparison.Ordinal)) &&
                kv.Value.Doc.Leaves().First(l =>
                    l.Path == field || l.Path.EndsWith(suffix, StringComparison.Ordinal))
                    .Node.GetValue<string>() == value)
            .Select(kv => kv.Key)
            .ToList());
    }
}

/// In-memory pricing store — mirrors the real repo's semantics
/// (hash dedup, due-row filter, completion/DLQ transitions) so the
/// API e2e and the processor's retry logic run hermetically. Rows
/// carry the enqueuing TenantContext — like the real per-tenant
/// files, a drain sees only its own tenant's outbox.
public sealed class FakePricingRepository
    : Lola.Application.Pricing.IPricingRepository
{
    public Dictionary<string, Lola.Application.Pricing.PricingEvalRecord>
        Evals
    { get; } = new();
    public Dictionary<string, Lola.Application.Pricing.PricingOutboxRow>
        Outbox
    { get; } = new();
    private readonly Dictionary<string, string> _byHash = new();
    private readonly Dictionary<string, string?> _outboxTenant = new();

    public Task<Lola.Application.Pricing.EnqueueResult> EnqueueAsync(
        Lola.Application.Pricing.PricingEvalRecord eval,
        string outboxId, string kind, string payloadJson,
        CancellationToken ct = default)
    {
        if (_byHash.TryGetValue(eval.RequestHash, out var existing))
            return Task.FromResult(
                new Lola.Application.Pricing.EnqueueResult(existing, true));
        eval = eval with
        {
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        Evals[eval.EvalId] = eval;
        Outbox[outboxId] = new Lola.Application.Pricing.PricingOutboxRow(
            outboxId, kind, eval.EvalId, eval.RequestHash, payloadJson,
            "pending", 0, null, null);
        _outboxTenant[outboxId] =
            Lola.Application.Tenancy.TenantContext.Current?.TenantId;
        _byHash[eval.RequestHash] = eval.EvalId;
        return Task.FromResult(
            new Lola.Application.Pricing.EnqueueResult(eval.EvalId, false));
    }

    public Task<Lola.Application.Pricing.PricingEvalRecord?> GetEvalAsync(
        string evalId, CancellationToken ct = default)
        => Task.FromResult(Evals.GetValueOrDefault(evalId));

    public Task<Lola.Application.Pricing.PricingEvalRecord?>
        GetLatestEvalAsync(string appId, CancellationToken ct = default)
        => Task.FromResult(Evals.Values
            .Where(e => e.ApplicationId == appId)
            .OrderByDescending(e => e.CreatedUtc ?? e.EvalId)
            .FirstOrDefault());

    public Task<IReadOnlyList<Lola.Application.Pricing.PricingEvalRecord>>
        ListEvalsAsync(string appId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Pricing.PricingEvalRecord>>(
            Evals.Values.Where(e => e.ApplicationId == appId)
                .OrderBy(e => e.CreatedUtc ?? e.EvalId).ToList());

    public Task<IReadOnlyList<Lola.Application.Pricing.PricingOutboxRow>>
        ListDueOutboxAsync(string nowUtc, int limit,
            CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Pricing.PricingOutboxRow>>(
            Outbox.Values.Where(r => r.Status == "pending" &&
                _outboxTenant.GetValueOrDefault(r.OutboxId) ==
                    Lola.Application.Tenancy.TenantContext.Current?.TenantId &&
                (r.NextRetryUtc is null ||
                 string.CompareOrdinal(r.NextRetryUtc, nowUtc) <= 0))
                .Take(limit).ToList());

    public Task CompleteAsync(string outboxId, string evalId,
        string adapter, string resultJson, string completedUtc,
        CancellationToken ct = default)
    {
        Outbox[outboxId] = Outbox[outboxId] with { Status = "done" };
        Evals[evalId] = Evals[evalId] with
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
        Outbox[outboxId] = Outbox[outboxId] with
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
        Outbox[outboxId] = Outbox[outboxId] with
        {
            Status = "dlq",
            Attempts = attempts,
            LastError = error
        };
        Evals[evalId] = Evals[evalId] with
        { Status = "failed", Error = error };
        return Task.CompletedTask;
    }
}
