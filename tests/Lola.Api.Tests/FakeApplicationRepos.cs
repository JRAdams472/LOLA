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

/// In-memory rate-lock store — tenant-scoped like the pricing fake
/// so parallel test tenants can't see each other's locks.
public sealed class FakeRateLockRepository
    : Lola.Application.Pricing.IRateLockRepository
{
    public Dictionary<string, Lola.Application.Pricing.RateLockRecord>
        Rows
    { get; } = new();

    private static string? Tenant =>
        Lola.Application.Tenancy.TenantContext.Current?.TenantId;
    private readonly Dictionary<string, string?> _tenant = new();

    public Task InsertAsync(
        Lola.Application.Pricing.RateLockRecord row,
        CancellationToken ct = default)
    {
        Rows[row.LockId] = row;
        _tenant[row.LockId] = Tenant;
        return Task.CompletedTask;
    }

    public Task<Lola.Application.Pricing.RateLockRecord?> GetActiveAsync(
        string appId, CancellationToken ct = default)
        => Task.FromResult(Rows.Values.FirstOrDefault(r =>
            r.ApplicationId == appId && r.Status == "active" &&
            _tenant.GetValueOrDefault(r.LockId) == Tenant));

    public Task<IReadOnlyList<Lola.Application.Pricing.RateLockRecord>>
        ListAsync(string appId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Pricing.RateLockRecord>>(
            Rows.Values.Where(r => r.ApplicationId == appId &&
                _tenant.GetValueOrDefault(r.LockId) == Tenant)
                .OrderBy(r => r.LockedUtc).ToList());

    public Task UnlockAsync(string lockId, string reason,
        string unlockedBy, string unlockedUtc,
        CancellationToken ct = default)
    {
        var r = Rows[lockId];
        Rows[lockId] = r with
        {
            Status = "unlocked",
            UnlockReason = reason,
            UnlockedBy = unlockedBy,
            UnlockedUtc = unlockedUtc
        };
        return Task.CompletedTask;
    }
}

/// In-memory fee eval/outbox store with the same semantics as the
/// real FeeRepository (hash dedup, due-row filter, completion/DLQ
/// transitions, item capture) so the API e2e and the processor's
/// retry logic run hermetically. Rows carry the enqueuing
/// TenantContext — a drain sees only its own tenant's outbox.
public sealed class FakeFeeRepository
    : Lola.Application.Fees.IFeeRepository
{
    public Dictionary<string, Lola.Application.Fees.FeeEvalRecord>
        Evals
    { get; } = new();
    public Dictionary<string, Lola.Application.Fees.FeeOutboxRow>
        Outbox
    { get; } = new();
    public List<Lola.Application.Fees.FeeItemRecord> Items { get; } = [];
    private readonly Dictionary<string, string> _byHash = new();
    private readonly Dictionary<string, string?> _outboxTenant = new();

    public Task<Lola.Application.Fees.FeeEnqueueResult> EnqueueAsync(
        Lola.Application.Fees.FeeEvalRecord eval, string outboxId,
        string kind, string payloadJson, CancellationToken ct = default)
    {
        if (_byHash.TryGetValue(eval.RequestHash, out var existing))
            return Task.FromResult(
                new Lola.Application.Fees.FeeEnqueueResult(
                    existing, true));
        eval = eval with
        {
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        Evals[eval.EvalId] = eval;
        Outbox[outboxId] = new Lola.Application.Fees.FeeOutboxRow(
            outboxId, kind, eval.EvalId, eval.RequestHash, payloadJson,
            "pending", 0, null, null);
        _outboxTenant[outboxId] =
            Lola.Application.Tenancy.TenantContext.Current?.TenantId;
        _byHash[eval.RequestHash] = eval.EvalId;
        return Task.FromResult(
            new Lola.Application.Fees.FeeEnqueueResult(
                eval.EvalId, false));
    }

    public Task<Lola.Application.Fees.FeeEvalRecord?> GetEvalAsync(
        string evalId, CancellationToken ct = default)
        => Task.FromResult(Evals.GetValueOrDefault(evalId));

    public Task<Lola.Application.Fees.FeeEvalRecord?>
        GetLatestEvalAsync(string appId, CancellationToken ct = default)
        => Task.FromResult(Evals.Values
            .Where(e => e.ApplicationId == appId)
            .OrderByDescending(e => e.CreatedUtc ?? e.EvalId)
            .FirstOrDefault());

    public Task<IReadOnlyList<Lola.Application.Fees.FeeEvalRecord>>
        ListEvalsAsync(string appId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Fees.FeeEvalRecord>>(
            Evals.Values.Where(e => e.ApplicationId == appId)
                .OrderBy(e => e.CreatedUtc ?? e.EvalId).ToList());

    public Task<IReadOnlyList<Lola.Application.Fees.FeeOutboxRow>>
        ListDueOutboxAsync(string nowUtc, int limit,
            CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Fees.FeeOutboxRow>>(
            Outbox.Values.Where(r => r.Status == "pending" &&
                _outboxTenant.GetValueOrDefault(r.OutboxId) ==
                    Lola.Application.Tenancy.TenantContext.Current?.TenantId &&
                (r.NextRetryUtc is null ||
                 string.CompareOrdinal(r.NextRetryUtc, nowUtc) <= 0))
                .Take(limit).ToList());

    public Task CompleteAsync(string outboxId, string evalId,
        string adapter, string resultJson,
        IReadOnlyList<Lola.Application.Fees.FeeItemRecord> items,
        string completedUtc, CancellationToken ct = default)
    {
        Outbox[outboxId] = Outbox[outboxId] with { Status = "done" };
        Evals[evalId] = Evals[evalId] with
        {
            Status = "done",
            Adapter = adapter,
            ResultJson = resultJson,
            CompletedUtc = completedUtc
        };
        Items.AddRange(items);
        // Same merge as the real repo: overrides whose (feeCode,
        // section) isn't in this eval's items go stale.
        var appId2 = Evals[evalId].ApplicationId;
        var live = items.Select(i => (i.FeeCode, i.Section))
            .ToHashSet();
        var variant = Evals[evalId].Variant;
        foreach (var o in Overrides.Values
            .Where(o => o.ApplicationId == appId2 &&
                o.Variant == variant &&
                o.ClearedUtc is null).ToList())
            Overrides[o.OverrideId] = o with
            { Stale = live.Contains((o.FeeCode, o.Section)) ? 0L : 1L };
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

    public Task<Lola.Application.Fees.FeeItemLookup?> GetItemAsync(
        string itemId, CancellationToken ct = default)
    {
        var item = Items.FirstOrDefault(i => i.ItemId == itemId);
        if (item is null)
            return Task.FromResult<Lola.Application.Fees
                .FeeItemLookup?>(null);
        var ev = Evals.TryGetValue(item.EvalId, out var e)
            ? e : null;
        return Task.FromResult<Lola.Application.Fees.FeeItemLookup?>(
            new(item, ev?.ApplicationId ?? "", ev?.Variant ?? ""));
    }

    public Task<IReadOnlyList<Lola.Application.Fees.FeeItemRecord>>
        ListItemsAsync(string evalId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Fees.FeeItemRecord>>(
            Items.Where(i => i.EvalId == evalId).ToList());

    public Dictionary<string, Lola.Application.Fees.FeeOverrideRecord>
        Overrides
    { get; } = new();

    public Task<Lola.Application.Fees.FeeOverrideRecord?>
        GetOverrideAsync(string overrideId,
            CancellationToken ct = default)
        => Task.FromResult(Overrides.GetValueOrDefault(overrideId));

    public Task<Lola.Application.Fees.FeeOverrideRecord>
        UpsertOverrideAsync(Lola.Application.Fees.FeeOverrideRecord o,
            CancellationToken ct = default)
    {
        var existing = Overrides.Values.FirstOrDefault(x =>
            x.ApplicationId == o.ApplicationId &&
            x.Variant == o.Variant &&
            x.FeeCode == o.FeeCode && x.Section == o.Section &&
            x.ClearedUtc is null);
        var merged = existing is null ? o
            : o with { OverrideId = existing.OverrideId };
        Overrides[merged.OverrideId] = merged;
        return Task.FromResult(merged);
    }

    public Task ClearOverrideAsync(string overrideId, string clearedUtc,
        CancellationToken ct = default)
    {
        Overrides[overrideId] = Overrides[overrideId] with
        { ClearedUtc = clearedUtc };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Lola.Application.Fees.FeeOverrideRecord>>
        ListOverridesAsync(string appId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Fees.FeeOverrideRecord>>(
            Overrides.Values.Where(o => o.ApplicationId == appId &&
                o.ClearedUtc is null).ToList());

    public Task<IReadOnlyList<Lola.Application.Fees.FeeEvalRecord>>
        GetLatestBatchEvalsAsync(string appId,
            CancellationToken ct = default)
    {
        var latest = Evals.Values
            .Where(e => e.ApplicationId == appId)
            .OrderByDescending(e => e.CreatedUtc).FirstOrDefault();
        if (latest is null)
            return Task.FromResult<IReadOnlyList<
                Lola.Application.Fees.FeeEvalRecord>>([]);
        var key = latest.BatchId ?? latest.EvalId;
        return Task.FromResult<IReadOnlyList<
            Lola.Application.Fees.FeeEvalRecord>>(
            Evals.Values.Where(e => e.ApplicationId == appId &&
                (e.BatchId ?? e.EvalId) == key)
                .OrderBy(e => e.CreatedUtc).ToList());
    }
}

/// Hermetic document store for the API e2e (MPE-190) � mirrors
/// DocRepository semantics: enqueue dedupes on request_hash, due
/// rows join the doc's request id for the poll phase, and the
/// outbox payload is stored "ciphertext-at-rest" via a FakeCipher
/// tag so nothing plaintext-sensitive sits in the fake either.
public sealed class FakeDocumentRepository
    : Lola.Application.Documents.IDocumentRepository
{
    public Dictionary<string, Lola.Application.Documents.DocRecord> Docs { get; } = [];
    public Dictionary<string, Lola.Application.Documents.DocOutboxRow> Outbox { get; } = [];
    // Tenant scoping � the real store is a per-tenant file; the fake
    // captures TenantContext at enqueue and filters due rows by it so
    // one test's rows can't drain inside another test's tenant.
    private readonly Dictionary<string, string> _rowTenants = [];

    public Task<Lola.Application.Documents.DocEnqueueResult> EnqueueAsync(
        Lola.Application.Documents.DocRecord doc, string outboxId,
        string kind, string payloadJson,
        CancellationToken ct = default)
    {
        var dup = Outbox.Values.FirstOrDefault(
            r => r.RequestHash == doc.RequestHash);
        if (dup is not null)
            return Task.FromResult(
                new Lola.Application.Documents.DocEnqueueResult(
                    dup.DocId, true));
        Docs[doc.DocId] = doc;
        _rowTenants[outboxId] =
            Lola.Application.Tenancy.TenantContext.Current?.TenantId
            ?? "";
        Outbox[outboxId] = new Lola.Application.Documents.DocOutboxRow(
            outboxId, kind, doc.DocId, doc.RequestHash, payloadJson,
            "pending", 0, null, null);
        return Task.FromResult(
            new Lola.Application.Documents.DocEnqueueResult(
                doc.DocId, false));
    }

    public Task<Lola.Application.Documents.DocRecord?> GetDocAsync(
        string docId, CancellationToken ct = default)
        => Task.FromResult(
            Docs.TryGetValue(docId, out var d) ? d : null);

    public Task<IReadOnlyList<Lola.Application.Documents.DocRecord>>
        ListDocsAsync(string appId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Documents.DocRecord>>(
            Docs.Values.Where(d => d.ApplicationId == appId).ToList());

    public Task<IReadOnlyList<Lola.Application.Documents.DocOutboxRow>>
        ListDueOutboxAsync(string nowUtc, int limit,
            CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Documents.DocOutboxRow>>(
            Outbox.Values.Where(r => r.Status == "pending" &&
                _rowTenants[r.OutboxId] ==
                    (Lola.Application.Tenancy.TenantContext.Current
                        ?.TenantId ?? "") &&
                (r.NextRetryUtc is null ||
                 string.CompareOrdinal(r.NextRetryUtc, nowUtc) <= 0))
                .Take(limit)
                .Select(r => r with
                {
                    RequestId = Docs[r.DocId].RequestId
                }).ToList());

    public Task MarkGeneratingAsync(string outboxId, string docId,
        string requestId, string nextRetryUtc,
        CancellationToken ct = default)
    {
        Docs[docId] = Docs[docId] with
        { Status = "generating", RequestId = requestId };
        Outbox[outboxId] = Outbox[outboxId] with
        { NextRetryUtc = nextRetryUtc };
        return Task.CompletedTask;
    }

    public Task CompleteAsync(string outboxId, string docId,
        string requestId, string? filename, string completedUtc,
        CancellationToken ct = default)
    {
        Docs[docId] = Docs[docId] with
        {
            Status = "completed",
            Filename = filename,
            RequestId = requestId,
            CompletedUtc = completedUtc
        };
        Outbox[outboxId] = Outbox[outboxId] with { Status = "done" };
        return Task.CompletedTask;
    }

    public Task RescheduleAsync(string outboxId, string docId,
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

    public Task DeadLetterAsync(string outboxId, string docId,
        long attempts, string error,
        CancellationToken ct = default)
    {
        Docs[docId] = Docs[docId] with
        { Status = "failed", Error = error };
        Outbox[outboxId] = Outbox[outboxId] with
        { Status = "dlq", Attempts = attempts, LastError = error };
        return Task.CompletedTask;
    }
}
