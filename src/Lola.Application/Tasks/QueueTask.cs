namespace Lola.Application.Tasks;

/// Task kinds — the dimensions the evaluator (M16 P2) derives work
/// from; `manual` is staff-created.
public static class TaskKind
{
    public const string ConditionBlocking = "condition_blocking";
    public const string StepReview = "step_review";
    public const string TridDeadline = "trid_deadline";
    public const string StaleDraft = "stale_draft";
    public const string Manual = "manual";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        ConditionBlocking, StepReview, TridDeadline, StaleDraft, Manual
    };
}

/// Task states: open → claimed → done; open|claimed → canceled when
/// the source resolves or the task is withdrawn. `done` and `canceled`
/// are terminal.
public static class TaskState
{
    public const string Open = "open";
    public const string Claimed = "claimed";
    public const string Done = "done";
    public const string Canceled = "canceled";
}

/// Priority constants — higher ranks first; due_utc breaks ties.
public static class TaskPriority
{
    public const int Low = 10;
    public const int Normal = 30;
    public const int High = 50;
    public const int Urgent = 80;
}

/// Values for <see cref="QueueTask.BlockingEntity"/> — which source
/// dimension produced the task (its dedupe key with kind + ref id).
public static class TaskSource
{
    public const string Condition = "condition";
    public const string StepEvent = "step_event";
    public const string TridClock = "trid_clock";
    public const string DraftAge = "draft_age";
}

/// A queued work item in the control plane (M16). PII-free by
/// construction — refs and operational metadata only; the PII-scan
/// tests pin this contract. `Seq` is the monotonic row order used to
/// break ranking ties deterministically.
public sealed record QueueTask(
    long Seq, string TaskId, string TenantId, string ApplicationId,
    string Kind, string Title, int Priority,
    IReadOnlyList<string> SkillTags, string? DueUtc, string State,
    string? BlockingEntity, string? BlockingEntityId,
    string? ClaimedBy, string CreatedUtc, string? ClaimedUtc,
    string? CompletedUtc, string UpdatedUtc);

/// Fields a caller may supply when deriving or creating a task. The
/// evaluator sets <see cref="BlockingEntity"/>/<see cref="BlockingEntityId"/>
/// for derived tasks; manual tasks leave them null.
public sealed record TaskDraft(
    string TenantId, string ApplicationId, string Kind, string Title,
    int Priority = TaskPriority.Normal, IReadOnlyList<string>? SkillTags = null,
    string? DueUtc = null, string? BlockingEntity = null,
    string? BlockingEntityId = null);
