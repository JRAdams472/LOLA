namespace Lola.Application.Tasks;

/// The control-plane work queue (M16). Tenant-side evaluators upsert
/// PII-free projections; the officer surface reads the ranked queue
/// and transitions tasks. Claim/complete audit writes live in the
/// handlers (control-plane audit), matching the conditions split.
public interface ITaskRepository
{
    /// Upsert a derived task. The dedupe key is
    /// (tenant, application, kind, blocking ref): an existing
    /// open/claimed row gets its metadata refreshed (title, priority,
    /// skills, due) without touching claim state; otherwise a new
    /// open row is inserted.
    Task<QueueTask> UpsertAsync(TaskDraft draft,
        CancellationToken ct = default);
    Task<QueueTask?> GetAsync(string taskId,
        CancellationToken ct = default);
    /// Ranked live queue for a tenant — open+claimed, priority desc,
    /// due_utc asc, seq asc. When <paramref name="skills"/> is
    /// non-empty, only tasks sharing at least one tag are returned;
    /// untagged tasks are general work and match every filter.
    Task<IReadOnlyList<QueueTask>> ListQueueAsync(string tenantId,
        IReadOnlyList<string>? skills = null,
        CancellationToken ct = default);
    /// open → claimed under <paramref name="actor"/>. Returns null
    /// when the task is not claimable (claimed by anyone, done,
    /// canceled); the caller re-reads to build the error.
    Task<QueueTask?> ClaimAsync(string taskId, string actor,
        CancellationToken ct = default);
    /// open|claimed → done. Returns null for terminal/unknown tasks.
    Task<QueueTask?> CompleteAsync(string taskId, string actor,
        CancellationToken ct = default);
    /// Resolve live tasks derived from a cleared source (e.g. a
    /// condition closing) — open|claimed → canceled. Returns the
    /// count resolved.
    Task<int> ResolveBySourceAsync(string tenantId,
        string applicationId, string blockingEntity,
        string blockingEntityId, CancellationToken ct = default);
}
