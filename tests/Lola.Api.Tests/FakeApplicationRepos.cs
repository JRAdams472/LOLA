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

    /// application_partners mirror (M19) — key is (appId, partnerUserId).
    public ConcurrentDictionary<(string AppId, string UserId), PartnerLink>
        PartnerLinks
    { get; } = new();
}

public sealed class FakeApplicationRepository(FakeApplicationStore store)
    : IApplicationRepository
{
    /// In-memory mirror of the scoped query (MPE-266): the scope
    /// predicate filters before paging; `includeNames=false` stamps
    /// the masked sentinel without surfacing the stored name.
    public Task<ApplicationPage> ListScopedAsync(
        ActorScope scope, string? actorId, int skip, int take,
        bool includeNames, CancellationToken ct = default)
    {
        var rows = store.Apps
            .Where(kv => scope == ActorScope.Staff ||
                (scope == ActorScope.Borrower &&
                    kv.Value.BorrowerUserId == actorId) ||
                (scope == ActorScope.Partner &&
                    store.PartnerLinks.TryGetValue(
                        (kv.Key, actorId!), out var l) &&
                    l.RevokedUtc is null))
            .OrderByDescending(kv => kv.Value.CreatedUtc)
            .ThenBy(kv => kv.Key)
            .ToList();
        var items = rows.Skip(skip).Take(take)
            .Select(kv => new ApplicationMeta(kv.Key, kv.Value.Status,
                kv.Value.PipelineStep, kv.Value.BorrowerUserId,
                kv.Value.Version, kv.Value.TridClockStart,
                includeNames ? BorrowerName(kv.Value.Doc)
                    : BorrowerName(kv.Value.Doc) is null ? null
                        : ApplicationMeta.MaskedName, null))
            .ToList();
        return Task.FromResult(new ApplicationPage(
            items, rows.Count, skip, take));
    }

    public Task<ScopedApplicationMeta?> GetScopedMetaAsync(
        string id, ActorScope scope, string? actorId,
        CancellationToken ct = default)
    {
        if (!store.Apps.TryGetValue(id, out var app))
            return Task.FromResult<ScopedApplicationMeta?>(null);
        var inScope = scope switch
        {
            ActorScope.Staff => true,
            ActorScope.Borrower => app.BorrowerUserId == actorId,
            _ => store.PartnerLinks.TryGetValue((id, actorId!),
                out var l) && l.RevokedUtc is null,
        };
        return Task.FromResult<ScopedApplicationMeta?>(
            new ScopedApplicationMeta(id, app.Status, app.PipelineStep,
                app.BorrowerUserId, app.Version, app.TridClockStart,
                app.SubmittedUtc, inScope));
    }

    public Task<string?> GetDocValueAsync(
        string id, string path, CancellationToken ct = default)
        => Task.FromResult(store.Apps.TryGetValue(id, out var app)
            ? app.Doc.Get(path.TrimStart('$', '.'))?.GetValue<string>()
            : null);

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

    /// Mirrors the real seam's contract: draft-only, merge against the
    /// stored doc, bump version — the in-memory store serializes on the
    /// dictionary like the real file serializes on BEGIN IMMEDIATE.
    public Task<(int Version, ApplicationDocument Doc)> PatchDraftAsync(
        string id, IReadOnlyDictionary<string, string> patch,
        string writtenBy, CancellationToken ct = default)
    {
        var app = store.Apps[id];
        if (app.Status != "draft")
            throw Lola.Application.Errors.DomainException.BadRequest(
                "only drafts can be patched");
        var doc = app.Doc;
        foreach (var (path, value) in patch)
            doc.Set(path, value);
        app.Version++;
        return Task.FromResult((app.Version, doc));
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
                kv.Value.Version, kv.Value.TridClockStart,
                BorrowerName(kv.Value.Doc), null))
            .ToList());

    public Task<bool> IsPartnerLinkedAsync(string id,
        string partnerUserId, CancellationToken ct = default)
        => Task.FromResult(store.PartnerLinks.TryGetValue(
            (id, partnerUserId), out var l) && l.RevokedUtc is null);

    public Task<IReadOnlyList<string>> LinkedAppIdsForPartnerAsync(
        string partnerUserId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(store.PartnerLinks
            .Where(kv => kv.Key.UserId == partnerUserId
                && kv.Value.RevokedUtc is null)
            .Select(kv => kv.Key.AppId).ToList());

    /// Mirrors the real repo's party[0] first+last composition — the
    /// fake has no ciphertext, so it reads the stored doc directly.
    private static string? BorrowerName(ApplicationDocument doc)
    {
        var name = string.Join(' ', new[]
            {
                doc.Get("party[0].first_name")?.GetValue<string>(),
                doc.Get("party[0].last_name")?.GetValue<string>()
            }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return name.Length == 0 ? null : name;
    }
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
    // Insertion sequence — the real store's newest-first read
    // tiebreaks on rowid; fake records carry no created_utc, so the
    // sequence number mirrors rowid ordering.
    private long _seq;
    private readonly Dictionary<string, long> _docSeq = [];

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
        _docSeq[doc.DocId] = ++_seq;
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
            Docs.Values.Where(d => d.ApplicationId == appId)
                .OrderByDescending(d => _docSeq[d.DocId]).ToList());

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

/// Hermetic vault store for the API e2e (MPE-200) — mirrors
/// VaultRepository semantics: enqueue dedupes on request_hash,
/// outbox rows carry the ambient tenant like the doc fake so one
/// test's scans can't drain inside another's tenant, and the
/// insertion sequence mirrors rowid ordering for newest-first lists.
public sealed class FakeVaultDocRepository
    : Lola.Application.Vault.IVaultDocRepository
{
    public Task<IReadOnlyList<Lola.Application.Vault.VaultDocRecord>>
        ListForUploaderAsync(string appId, string uploaderId,
            CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Vault.VaultDocRecord>>(Docs.Values
                .Where(d => d.ApplicationId == appId &&
                    d.Actor == uploaderId)
                .OrderByDescending(d => d.CreatedUtc).ToList());

    public Dictionary<string, Lola.Application.Vault.VaultDocRecord> Docs { get; } = [];
    public Dictionary<string, Lola.Application.Vault.VaultScanOutboxRow> Outbox { get; } = [];
    private readonly Dictionary<string, string> _rowTenants = [];
    private long _seq;
    private readonly Dictionary<string, long> _docSeq = [];

    public Task<Lola.Application.Vault.VaultEnqueueResult> EnqueueAsync(
        Lola.Application.Vault.VaultDocRecord doc, string outboxId,
        CancellationToken ct = default)
    {
        var dup = Docs.Values.FirstOrDefault(
            r => r.RequestHash == doc.RequestHash);
        if (dup is not null)
            return Task.FromResult(
                new Lola.Application.Vault.VaultEnqueueResult(
                    dup.VaultDocId, true));
        Docs[doc.VaultDocId] = doc;
        _docSeq[doc.VaultDocId] = ++_seq;
        _rowTenants[outboxId] =
            Lola.Application.Tenancy.TenantContext.Current?.TenantId
            ?? "";
        Outbox[outboxId] = new Lola.Application.Vault.VaultScanOutboxRow(
            outboxId, doc.VaultDocId, doc.RequestHash, "pending",
            0, null, null);
        return Task.FromResult(
            new Lola.Application.Vault.VaultEnqueueResult(
                doc.VaultDocId, false));
    }

    public Task<Lola.Application.Vault.VaultDocRecord?> GetAsync(
        string vaultDocId, CancellationToken ct = default)
        => Task.FromResult(
            Docs.TryGetValue(vaultDocId, out var d) ? d : null);

    public Task<IReadOnlyList<Lola.Application.Vault.VaultDocRecord>>
        ListAsync(string appId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Vault.VaultDocRecord>>(
            Docs.Values.Where(d => d.ApplicationId == appId)
                .OrderByDescending(d => _docSeq[d.VaultDocId]).ToList());

    public Task<IReadOnlyList<Lola.Application.Vault.VaultScanOutboxRow>>
        ListDueScansAsync(string nowUtc, int limit,
            CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Vault.VaultScanOutboxRow>>(
            Outbox.Values.Where(r => r.Status == "pending" &&
                _rowTenants[r.OutboxId] ==
                    (Lola.Application.Tenancy.TenantContext.Current
                        ?.TenantId ?? "") &&
                (r.NextRetryUtc is null ||
                 string.CompareOrdinal(r.NextRetryUtc, nowUtc) <= 0))
                .Take(limit).ToList());

    public Task MarkScanningAsync(string outboxId, string nextRetryUtc,
        CancellationToken ct = default)
    {
        Outbox[outboxId] = Outbox[outboxId] with
        { NextRetryUtc = nextRetryUtc };
        return Task.CompletedTask;
    }

    public Task CompleteScanAsync(string outboxId, string vaultDocId,
        string cleanKey, string scannedUtc,
        CancellationToken ct = default)
    {
        Docs[vaultDocId] = Docs[vaultDocId] with
        { Status = "clean", S3Key = cleanKey, ScannedUtc = scannedUtc };
        Outbox[outboxId] = Outbox[outboxId] with { Status = "done" };
        return Task.CompletedTask;
    }

    public Task MarkInfectedAsync(string outboxId, string vaultDocId,
        string signature, string scannedUtc,
        CancellationToken ct = default)
    {
        Docs[vaultDocId] = Docs[vaultDocId] with
        {
            Status = "infected",
            Error = signature,
            ScannedUtc = scannedUtc
        };
        Outbox[outboxId] = Outbox[outboxId] with { Status = "done" };
        return Task.CompletedTask;
    }

    public Task MarkUnavailableAsync(string outboxId, string vaultDocId,
        string error, CancellationToken ct = default)
    {
        Docs[vaultDocId] = Docs[vaultDocId] with
        { Status = "unavailable", Error = error };
        Outbox[outboxId] = Outbox[outboxId] with
        { Status = "dlq", LastError = error };
        return Task.CompletedTask;
    }

    public Task RescheduleScanAsync(string outboxId, long attempts,
        string nextRetryUtc, string error,
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

    public Task SaveClassificationAsync(string vaultDocId,
        string classificationJson, string classifiedUtc,
        CancellationToken ct = default)
    {
        Docs[vaultDocId] = Docs[vaultDocId] with
        {
            ClassificationJson = classificationJson,
            ClassifiedUtc = classifiedUtc
        };
        return Task.CompletedTask;
    }
}

/// In-memory application_partners write side (M19) — same upsert/
/// soft-revoke semantics as the real repo: re-link clears revoked,
/// revoke reports false when nothing active exists.
public sealed class FakeApplicationPartnerRepository(
    FakeApplicationStore store, FakeTenantAuditLog audit,
    FakeLoanEventLog events)
    : IApplicationPartnerRepository
{
    /// Atomic-commit mirror (MPE-274): the in-memory store is
    /// single-threaded per call, so state+evidence apply together;
    /// NoOp/NotFound write nothing.
    public async Task<PartnerLinkOutcome> CommitLinkAsync(
        PartnerLink link, Lola.Application.Audit.AuditDraft auditDraft,
        Lola.Application.Events.LoanEventDraft loanEvent,
        CancellationToken ct = default)
    {
        if (store.PartnerLinks.TryGetValue(
                (link.AppId, link.PartnerUserId), out var cur) &&
            cur.RevokedUtc is null && cur.PartnerRole == link.PartnerRole)
            return PartnerLinkOutcome.NoOp;
        store.PartnerLinks[(link.AppId, link.PartnerUserId)] =
            link with { RevokedUtc = null };
        await audit.WriteAsync(auditDraft.Actor, auditDraft.Action,
            auditDraft.Entity, auditDraft.EntityId,
            auditDraft.FieldClasses, auditDraft.Reason, ct);
        await events.WriteAsync(loanEvent, ct);
        return PartnerLinkOutcome.Linked;
    }

    public async Task<PartnerLinkOutcome> CommitRevokeAsync(
        string appId, string partnerUserId, string revokedUtc,
        Lola.Application.Audit.AuditDraft auditDraft,
        Lola.Application.Events.LoanEventDraft loanEvent,
        CancellationToken ct = default)
    {
        if (!store.PartnerLinks.TryGetValue((appId, partnerUserId),
                out var existing) || existing.RevokedUtc is not null)
            return PartnerLinkOutcome.NotFound;
        store.PartnerLinks[(appId, partnerUserId)] =
            existing with { RevokedUtc = revokedUtc };
        await audit.WriteAsync(auditDraft.Actor, auditDraft.Action,
            auditDraft.Entity, auditDraft.EntityId,
            auditDraft.FieldClasses, auditDraft.Reason, ct);
        await events.WriteAsync(loanEvent, ct);
        return PartnerLinkOutcome.Revoked;
    }

    public Task LinkAsync(PartnerLink link, CancellationToken ct = default)
    {
        store.PartnerLinks[(link.AppId, link.PartnerUserId)] =
            link with { RevokedUtc = null };
        return Task.CompletedTask;
    }

    public Task<bool> RevokeAsync(string appId, string partnerUserId,
        string revokedUtc, CancellationToken ct = default)
    {
        if (!store.PartnerLinks.TryGetValue((appId, partnerUserId),
                out var existing) || existing.RevokedUtc is not null)
            return Task.FromResult(false);
        store.PartnerLinks[(appId, partnerUserId)] =
            existing with { RevokedUtc = revokedUtc };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<PartnerLink>> ListForAppAsync(
        string appId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PartnerLink>>(store.PartnerLinks
            .Where(kv => kv.Key.AppId == appId)
            .OrderByDescending(kv => kv.Value.LinkedUtc)
            .Select(kv => kv.Value).ToList());
}

/// Hermetic tolerance store for the API e2e (MPE-298) — mirrors
/// ToleranceRepository semantics: version insert stamps pending
/// resets to the new version (one fact, never torn), seq assigns
/// monotonically per app+kind like MAX(seq)+1.
public sealed class FakeToleranceRepository
    : Lola.Application.Compliance.IToleranceRepository
{
    public List<Lola.Application.Compliance.DisclosureVersionRecord>
        Versions
    { get; } = [];
    public List<Lola.Application.Compliance.ToleranceResetRecord>
        Resets
    { get; } = [];
    public Dictionary<string,
        Lola.Application.Compliance.ToleranceClassRecord>
        Classes
    { get; } = new();

    public Task<Lola.Application.Compliance.DisclosureVersionRecord>
        AddVersionAsync(string appId, string kind, string evalId,
            string? docId, string? resetOfVersionId,
            CancellationToken ct)
    {
        var v = new Lola.Application.Compliance.DisclosureVersionRecord(
            $"dver-{Versions.Count + 1}", appId, kind, evalId, docId,
            Versions.Count(v2 =>
                v2.ApplicationId == appId && v2.Kind == kind) + 1,
            resetOfVersionId, "2026-01-01T00:00:00.000Z");
        Versions.Add(v);
        foreach (var r in Resets
            .Where(r => r.ApplicationId == appId &&
                r.RebasedVersionId is null).ToList())
            Resets[Resets.IndexOf(r)] = r with
            { RebasedVersionId = v.VersionId };
        return Task.FromResult(v);
    }

    public Task<IReadOnlyList<
        Lola.Application.Compliance.DisclosureVersionRecord>>
        ListVersionsAsync(string appId, string kind,
            CancellationToken ct)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Compliance.DisclosureVersionRecord>>(
            Versions.Where(v => v.ApplicationId == appId &&
                v.Kind == kind).OrderBy(v => v.Seq).ToList());

    public Task<Lola.Application.Compliance.DisclosureVersionRecord?>
        GetVersionByDocAsync(string docId, CancellationToken ct)
        => Task.FromResult(Versions
            .FirstOrDefault(v => v.DocId == docId));

    public Task<IReadOnlyDictionary<string,
        Lola.Application.Compliance.ToleranceClassRecord>>
        GetClassesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string,
            Lola.Application.Compliance.ToleranceClassRecord>>(Classes);

    public Task<Lola.Application.Compliance.ToleranceResetRecord>
        AddResetAsync(string appId, string reasonCode,
            string feeCodesJson, string actor, CancellationToken ct)
    {
        var r = new Lola.Application.Compliance.ToleranceResetRecord(
            $"tres-{Resets.Count + 1}", appId, reasonCode,
            feeCodesJson, null, actor, "2026-01-01T00:00:00.000Z");
        Resets.Add(r);
        return Task.FromResult(r);
    }

    public Task<IReadOnlyList<
        Lola.Application.Compliance.ToleranceResetRecord>>
        ListResetsAsync(string appId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Compliance.ToleranceResetRecord>>(
            Resets.Where(r => r.ApplicationId == appId).ToList());

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

/// Hermetic ATR/QM store for the API e2e (MPE-307) — mirrors
/// AtrQmRepository semantics: results append and list newest-first;
/// registries are test-controlled defaults (empty APOR/state rows
/// mean warn-verdict fixtures by construction).
public sealed class FakeAtrQmRepository
    : Lola.Application.Compliance.IAtrQmRepository
{
    public List<Lola.Application.Compliance.AtrResultRecord> Results
    { get; } = [];
    public List<Lola.Application.Compliance.AporRateRecord> Apors
    { get; } = [new("2025-01-01", 6.50m)];
    public Dictionary<string,
        Lola.Application.Compliance.PfClassRecord> PfClasses
    { get; } = new();
    public Dictionary<string,
        Lola.Application.Compliance.StateHighCostRecord> StateRules
    { get; } = new();

    public Task<Lola.Application.Compliance.AtrResultRecord>
        AddResultAsync(string appId, string? evalId,
            decimal? dti, decimal? apr, decimal? apor,
            string verdictsJson, string actor, CancellationToken ct)
    {
        var r = new Lola.Application.Compliance.AtrResultRecord(
            $"atr-{Results.Count + 1}", appId, evalId, dti, apr, apor,
            verdictsJson, actor, "2026-01-01T00:00:00.000Z");
        Results.Add(r);
        return Task.FromResult(r);
    }

    public Task<IReadOnlyList<
        Lola.Application.Compliance.AtrResultRecord>>
        ListResultsAsync(string appId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<
            Lola.Application.Compliance.AtrResultRecord>>(
            Results.Where(r => r.ApplicationId == appId)
                .OrderByDescending(r => r.CreatedUtc).ToList());

    public Task<Lola.Application.Compliance.AporRateRecord?>
        GetAporAsync(CancellationToken ct)
        => Task.FromResult(Apors
            .OrderByDescending(a => a.EffectiveDate)
            .FirstOrDefault());

    public Task<IReadOnlyDictionary<string,
        Lola.Application.Compliance.PfClassRecord>>
        GetPfClassesAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string,
            Lola.Application.Compliance.PfClassRecord>>(PfClasses);

    public Task<Lola.Application.Compliance.StateHighCostRecord?>
        GetStateRuleAsync(string stateCode, CancellationToken ct)
        => Task.FromResult(StateRules.TryGetValue(
            stateCode.ToUpperInvariant(), out var r) ? r : null);
}
