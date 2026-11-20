using Dapper;
using Lola.Application.Tasks;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Lola.ControlPlane;

/// Control-plane task queue (M16 P1) — PII-free operational rows.
/// Writes take BEGIN IMMEDIATE like the audit log: two officers
/// claiming the same task, or an evaluator upsert racing a claim,
/// serialize on the write lock instead of forking state.
public sealed class TaskQueue(ControlPlaneDb db) : ITaskRepository
{
    private static string Now()
        => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    public async Task<QueueTask> UpsertAsync(TaskDraft draft,
        CancellationToken ct = default)
    {
        var skillsJson = JsonSerializer.Serialize(draft.SkillTags ?? []);
        var sourceRef = draft.BlockingEntityId ?? string.Empty;
        var now = Now();
        await using var conn = db.Open();
        await conn.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            var existing = await conn.QuerySingleOrDefaultAsync<long?>(
                """
                SELECT seq FROM tasks
                WHERE tenant_id = @TenantId
                  AND application_id = @ApplicationId
                  AND kind = @Kind
                  AND COALESCE(blocking_entity_id, '') = @sourceRef
                  AND state IN ('open', 'claimed')
                """,
                new { draft.TenantId, draft.ApplicationId, draft.Kind, sourceRef });
            if (existing is not null)
            {
                // Metadata refresh only — claim state and identity
                // columns must not regress on re-evaluation.
                await conn.ExecuteAsync(
                    """
                    UPDATE tasks SET title = @Title, priority = @Priority,
                        skill_tags = @skillsJson, due_utc = @DueUtc,
                        blocking_entity = @BlockingEntity,
                        updated_utc = @now
                    WHERE seq = @existing
                    """,
                    new
                    {
                        draft.Title,
                        draft.Priority,
                        skillsJson,
                        draft.DueUtc,
                        draft.BlockingEntity,
                        now,
                        existing
                    });
            }
            else
            {
                var taskId = $"task-{Guid.NewGuid():N}";
                await conn.ExecuteAsync(
                    """
                    INSERT INTO tasks
                        (task_id, tenant_id, application_id, kind, title,
                         priority, skill_tags, due_utc, state,
                         blocking_entity, blocking_entity_id,
                         created_utc, updated_utc)
                    VALUES
                        (@taskId, @TenantId, @ApplicationId, @Kind, @Title,
                         @Priority, @skillsJson, @DueUtc, 'open',
                         @BlockingEntity, @BlockingEntityId,
                         @now, @now)
                    """,
                    new
                    {
                        taskId,
                        draft.TenantId,
                        draft.ApplicationId,
                        draft.Kind,
                        draft.Title,
                        draft.Priority,
                        skillsJson,
                        draft.DueUtc,
                        draft.BlockingEntity,
                        BlockingEntityId = draft.BlockingEntityId,
                        now
                    });
            }
            var row = await ReadByDedupeAsync(conn, draft, sourceRef);
            await conn.ExecuteAsync("COMMIT");
            return row!;
        }
        catch
        {
            await conn.ExecuteAsync("ROLLBACK");
            throw;
        }
    }

    public async Task<QueueTask?> GetAsync(string taskId,
        CancellationToken ct = default)
    {
        await using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            $"{Select} WHERE task_id = @taskId", new { taskId });
        return row?.ToTask();
    }

    public async Task<IReadOnlyList<QueueTask>> ListQueueAsync(
        string tenantId, IReadOnlyList<string>? skills = null,
        CancellationToken ct = default)
    {
        await using var conn = db.Open();
        var rows = await conn.QueryAsync<Row>(
            $"""
            {Select}
            WHERE tenant_id = @tenantId AND state IN ('open', 'claimed')
            ORDER BY priority DESC,
                     due_utc IS NULL, due_utc ASC, seq ASC
            """,
            new { tenantId });
        var tasks = rows.Select(r => r.ToTask()).ToList();
        if (skills is { Count: > 0 })
            tasks = tasks.Where(t => t.SkillTags.Any(skills.Contains)
                || t.SkillTags.Count == 0).ToList();
        return tasks;
    }

    public async Task<QueueTask?> ClaimAsync(string taskId, string actor,
        CancellationToken ct = default)
        => await TransitionAsync(taskId, actor, fromState: TaskState.Open,
            toState: TaskState.Claimed, ct);

    public async Task<QueueTask?> CompleteAsync(string taskId,
        string actor, CancellationToken ct = default)
    {
        var now = Now();
        await using var conn = db.Open();
        await conn.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            var row = await ReadAsync(conn, taskId);
            if (row is null || row.State is TaskState.Done
                or TaskState.Canceled)
            {
                await conn.ExecuteAsync("ROLLBACK");
                return null;
            }
            await conn.ExecuteAsync(
                """
                UPDATE tasks SET state = 'done', completed_utc = @now,
                    updated_utc = @now
                WHERE task_id = @taskId
                """,
                new { now, taskId });
            var updated = await ReadAsync(conn, taskId);
            await conn.ExecuteAsync("COMMIT");
            return updated;
        }
        catch
        {
            await conn.ExecuteAsync("ROLLBACK");
            throw;
        }
    }

    public async Task<int> ResolveBySourceAsync(string tenantId,
        string applicationId, string blockingEntity,
        string blockingEntityId, CancellationToken ct = default)
    {
        await using var conn = db.Open();
        return await conn.ExecuteAsync(
            """
            UPDATE tasks SET state = 'canceled', updated_utc = @now
            WHERE tenant_id = @tenantId
              AND application_id = @applicationId
              AND blocking_entity = @blockingEntity
              AND COALESCE(blocking_entity_id, '') = @blockingEntityId
              AND state IN ('open', 'claimed')
            """,
            new
            {
                now = Now(),
                tenantId,
                applicationId,
                blockingEntity,
                blockingEntityId
            });
    }

    private async Task<QueueTask?> TransitionAsync(string taskId,
        string actor, string fromState, string toState,
        CancellationToken ct)
    {
        var now = Now();
        await using var conn = db.Open();
        await conn.ExecuteAsync("BEGIN IMMEDIATE");
        try
        {
            var row = await ReadAsync(conn, taskId);
            if (row is null || row.State != fromState)
            {
                await conn.ExecuteAsync("ROLLBACK");
                return null;
            }
            await conn.ExecuteAsync(
                """
                UPDATE tasks SET state = @toState, claimed_by = @actor,
                    claimed_utc = @now, updated_utc = @now
                WHERE task_id = @taskId
                """,
                new { toState, actor, now, taskId });
            var updated = await ReadAsync(conn, taskId);
            await conn.ExecuteAsync("COMMIT");
            return updated;
        }
        catch
        {
            await conn.ExecuteAsync("ROLLBACK");
            throw;
        }
    }

    private const string Select =
        """
        SELECT seq AS Seq, task_id AS TaskId, tenant_id AS TenantId,
               application_id AS ApplicationId, kind AS Kind,
               title AS Title, priority AS Priority,
               skill_tags AS SkillTagsJson, due_utc AS DueUtc,
               state AS State, blocking_entity AS BlockingEntity,
               blocking_entity_id AS BlockingEntityId,
               claimed_by AS ClaimedBy, created_utc AS CreatedUtc,
               claimed_utc AS ClaimedUtc, completed_utc AS CompletedUtc,
               updated_utc AS UpdatedUtc
        FROM tasks
        """;

    private static async Task<QueueTask?> ReadAsync(SqliteConnection conn,
        string taskId)
    {
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            $"{Select} WHERE task_id = @taskId", new { taskId });
        return row?.ToTask();
    }

    private static async Task<QueueTask?> ReadByDedupeAsync(
        SqliteConnection conn, TaskDraft draft, string sourceRef)
    {
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            $"""
            {Select}
            WHERE tenant_id = @TenantId
              AND application_id = @ApplicationId
              AND kind = @Kind
              AND COALESCE(blocking_entity_id, '') = @sourceRef
            ORDER BY seq DESC LIMIT 1
            """,
            new { draft.TenantId, draft.ApplicationId, draft.Kind, sourceRef });
        return row?.ToTask();
    }

    private sealed class Row
    {
        public long Seq { get; set; }
        public string TaskId { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string ApplicationId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Title { get; set; } = "";
        public int Priority { get; set; }
        public string SkillTagsJson { get; set; } = "[]";
        public string? DueUtc { get; set; }
        public string State { get; set; } = "";
        public string? BlockingEntity { get; set; }
        public string? BlockingEntityId { get; set; }
        public string? ClaimedBy { get; set; }
        public string CreatedUtc { get; set; } = "";
        public string? ClaimedUtc { get; set; }
        public string? CompletedUtc { get; set; }
        public string UpdatedUtc { get; set; } = "";

        public QueueTask ToTask() => new(
            Seq, TaskId, TenantId, ApplicationId, Kind, Title, Priority,
            JsonSerializer.Deserialize<List<string>>(SkillTagsJson) ?? [],
            DueUtc, State, BlockingEntity, BlockingEntityId, ClaimedBy,
            CreatedUtc, ClaimedUtc, CompletedUtc, UpdatedUtc);
    }
}
