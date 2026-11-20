using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Domain.Applications;

namespace Lola.Application.Tasks;

/// Tenant-side derivation engine (M16 P2): reads tenant-file state —
/// open+blocking conditions, step-event outcomes, the TRID clock,
/// draft age — and upserts PII-free projections into the control-plane
/// queue. Called from mutation paths per application and lazily at
/// queue read per tenant, so time-driven work (TRID proximity, stale
/// drafts) stays current without a background worker.
///
/// Everything the evaluator emits is refs + generated titles — the
/// condition's free-text Title is deliberately NOT copied (staff
/// notes can name borrowers; tasks must not — invariant 2/3).
public sealed class TaskEvaluator(
    IConditionRepository conditions,
    IApplicationRepository apps,
    ITaskRepository tasks,
    TimeProvider clock)
{
    private static readonly TimeSpan StaleDraftAge = TimeSpan.FromDays(7);
    private static readonly TimeSpan TridUrgentWindow = TimeSpan.FromHours(24);

    /// Per-application evaluation — mutation-path entry point. Re-reads
    /// the tenant-side sources and converges the app's live tasks onto
    /// current state (derive what's missing, resolve what cleared).
    public async Task EvaluateApplicationAsync(string tenantId,
        string appId, CancellationToken ct = default)
    {
        var meta = (await apps.ListAsync(ct))
            .FirstOrDefault(m => m.Id == appId);
        await EvaluateConditionsAsync(tenantId, appId, ct);
        if (meta is null) return;
        await EvaluateTridAsync(tenantId, meta, ct);
        await EvaluateStaleDraftAsync(tenantId, meta, ct);
        await EvaluateStepEventsAsync(tenantId, meta, ct);
    }

    /// Tenant-wide evaluation — lazy refresh behind the queue read
    /// (M16 P3). One meta scan, then per-app convergence.
    public async Task EvaluateTenantAsync(string tenantId,
        CancellationToken ct = default)
    {
        foreach (var meta in await apps.ListAsync(ct))
        {
            await EvaluateConditionsAsync(tenantId, meta.Id, ct);
            await EvaluateTridAsync(tenantId, meta, ct);
            await EvaluateStaleDraftAsync(tenantId, meta, ct);
            await EvaluateStepEventsAsync(tenantId, meta, ct);
        }
    }

    private async Task EvaluateConditionsAsync(string tenantId,
        string appId, CancellationToken ct)
    {
        foreach (var c in await conditions.ListByApplicationAsync(
            appId, ct: ct))
        {
            if (!c.Blocking) continue;
            if (c.State == ConditionState.Open)
            {
                // M17: feedback-raised blockers surface as
                // validation_exception (High, skill by source) —
                // officers see vendor exceptions in the same ranked
                // queue, resolved convergently when the condition
                // closes. BlockingEntity stays `condition` so one
                // resolve path covers both kinds.
                var fromVendor =
                    c.LinkedEntity == Vendors.FeedbackLink.VendorFeedback;
                await tasks.UpsertAsync(new TaskDraft(
                    tenantId, appId,
                    fromVendor ? TaskKind.ValidationException
                        : TaskKind.ConditionBlocking,
                    fromVendor
                        ? $"Resolve vendor exception — {c.Kind}"
                        : $"Resolve blocking condition — {c.Kind}",
                    TaskPriority.High,
                    fromVendor ? SkillsForSource(c.OpenedBy)
                        : SkillsFor(c),
                    BlockingEntity: TaskSource.Condition,
                    BlockingEntityId: c.ConditionId), ct);
            }
            else
            {
                await tasks.ResolveBySourceAsync(tenantId, appId,
                    TaskSource.Condition, c.ConditionId, ct);
            }
        }
    }

    private async Task EvaluateTridAsync(string tenantId,
        ApplicationMeta meta, CancellationToken ct)
    {
        var deadline = TridClock.DeadlineUtc(meta.TridClockStart);
        // The LE-delivery task lives until the loan is decisioned;
        // crossing the deadline escalates rather than resolves —
        // an unmet TRID clock is exactly what the queue exists for.
        if (deadline is not null && meta.Status != "decisioned")
        {
            var due = DateTimeOffset.Parse(deadline);
            var priority = due - clock.GetUtcNow() <= TridUrgentWindow
                ? TaskPriority.Urgent : TaskPriority.High;
            await tasks.UpsertAsync(new TaskDraft(
                tenantId, meta.Id, TaskKind.TridDeadline,
                "Deliver Loan Estimate — TRID clock running",
                priority, [TaskSkills.Closing], deadline,
                TaskSource.TridClock, meta.Id), ct);
        }
        else
        {
            await tasks.ResolveBySourceAsync(tenantId, meta.Id,
                TaskSource.TridClock, meta.Id, ct);
        }
    }

    private async Task EvaluateStaleDraftAsync(string tenantId,
        ApplicationMeta meta, CancellationToken ct)
    {
        var stale = meta.Status == "draft"
            && meta.UpdatedUtc is not null
            && DateTimeOffset.TryParse(meta.UpdatedUtc, out var touched)
            && clock.GetUtcNow() - touched > StaleDraftAge;
        if (stale)
        {
            await tasks.UpsertAsync(new TaskDraft(
                tenantId, meta.Id, TaskKind.StaleDraft,
                "Follow up — draft inactive 7+ days",
                TaskPriority.Low, [TaskSkills.Processing],
                BlockingEntity: TaskSource.DraftAge,
                BlockingEntityId: meta.Id), ct);
        }
        else
        {
            await tasks.ResolveBySourceAsync(tenantId, meta.Id,
                TaskSource.DraftAge, meta.Id, ct);
        }
    }

    private async Task EvaluateStepEventsAsync(string tenantId,
        ApplicationMeta meta, CancellationToken ct)
    {
        var events = await apps.ListStepEventsAsync(meta.Id, ct);
        foreach (var g in events.GroupBy(e => e.Step))
        {
            // Step events are append-only and ordered — last write wins.
            var latest = g.Last();
            if (latest.Event is "failed" or "referred")
            {
                await tasks.UpsertAsync(new TaskDraft(
                    tenantId, meta.Id, TaskKind.StepReview,
                    $"Review {latest.Step} result — {latest.Event}",
                    TaskPriority.High, [TaskSkills.Processing],
                    BlockingEntity: TaskSource.StepEvent,
                    BlockingEntityId: latest.Step), ct);
            }
            else
            {
                await tasks.ResolveBySourceAsync(tenantId, meta.Id,
                    TaskSource.StepEvent, latest.Step, ct);
            }
        }
    }

    /// Vendor-exception skill by reporting source — `opened_by` is
    /// `auto:{source}` for feedback-raised conditions. mpex findings
    /// are pricing/eligibility (underwriting); isppr findings are
    /// document-package issues (processing); unknown sources fall back
    /// to underwriting like other data_validation work.
    private static IReadOnlyList<string> SkillsForSource(string openedBy)
        => openedBy.StartsWith("auto:", StringComparison.Ordinal)
            && openedBy["auto:".Length..] == "isppr"
                ? [TaskSkills.Processing]
                : [TaskSkills.Underwriting];

    private static IReadOnlyList<string> SkillsFor(Condition c)
        => c.Kind switch
        {
            ConditionKind.DocRequired => [TaskSkills.Processing],
            ConditionKind.DataValidation => [TaskSkills.Underwriting],
            ConditionKind.DistanceCheck => [TaskSkills.Underwriting],
            _ => []
        };
}
