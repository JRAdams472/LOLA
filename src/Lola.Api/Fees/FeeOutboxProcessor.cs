using Lola.Application.Fees;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Lola.Api.Fees;

/// Registry-scoped outbox drain (MPE-183) — one pass over every
/// active tenant file, executing due `fee_outbox` rows against the
/// configured adapter. Like TenantMigrationRunner this is a job
/// surface: the tenant comes from the registry record, not a
/// request, so each drain opens an explicit TenantContext scope
/// (same shape middleware uses) before any data-plane touch.
///
/// Retry policy: a row gets MaxAttempts executions with exponential
/// backoff (5s × 2ⁿ, capped at 5m); terminal attempts and
/// non-retryable adapter errors land in the DLQ and fail the eval.
public sealed class FeeOutboxProcessor(
    IServiceProvider services, ITenantRegistry registry,
    TimeProvider clock)
{
    private const int BatchSize = 25;
    private const int MaxAttempts = 5;
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        var drained = 0;
        foreach (var tenant in await registry.ListAsync(ct))
        {
            if (tenant.Status != "active" || tenant.DbKeyWrapped is null)
                continue;
            drained += await DrainTenantAsync(tenant, ct);
        }
        return drained;
    }

    public async Task<int> DrainTenantAsync(TenantRecord tenant,
        CancellationToken ct = default)
    {
        // Fresh scope per tenant — the scoped TenantConnectionFactory
        // caches the file key, so a shared scope would pin the FIRST
        // tenant's key forever.
        await using var scope = services.CreateAsyncScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<IFeeRepository>();
        var adapter = scope.ServiceProvider
            .GetRequiredService<IFeeService>();
        var processed = 0;
        using (TenantContext.Use(TenantScope.From(tenant)))
        {
            foreach (var row in await repo.ListDueOutboxAsync(
                         Now(), BatchSize, ct))
            {
                ct.ThrowIfCancellationRequested();
                await ProcessRowAsync(repo, adapter, row, ct);
                processed++;
            }
        }
        return processed;
    }

    private async Task ProcessRowAsync(IFeeRepository repo,
        IFeeService adapter, FeeOutboxRow row, CancellationToken ct)
    {
        var attempts = row.Attempts + 1;
        try
        {
            var payload = FeeWire.ParsePayload(row.PayloadJson)
                ?? throw new FeeAdapterException(FeeErrorKind.Contract,
                    "unparseable outbox payload");
            var result = await adapter.EvaluateAsync(
                payload.Scenario, payload.Options, ct);
            await repo.CompleteAsync(row.OutboxId, row.EvalId,
                adapter.Adapter, FeeWire.ResultJson(result),
                Items(row.EvalId, result), Now(), ct);
        }
        catch (FeeAdapterException ex)
            when (!ex.Retryable || attempts >= MaxAttempts)
        {
            await repo.DeadLetterAsync(row.OutboxId, row.EvalId,
                attempts, $"{ex.Kind}: {ex.Message}", ct);
        }
        catch (FeeAdapterException ex)
        {
            await repo.RescheduleAsync(row.OutboxId, row.EvalId,
                attempts, NowPlus(Backoff(attempts)),
                $"{ex.Kind}: {ex.Message}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (attempts >= MaxAttempts)
                await repo.DeadLetterAsync(row.OutboxId, row.EvalId,
                    attempts, $"unexpected: {ex.Message}", ct);
            else
                await repo.RescheduleAsync(row.OutboxId, row.EvalId,
                    attempts, NowPlus(Backoff(attempts)),
                    $"unexpected: {ex.Message}", ct);
        }
    }

    /// Persist one `fee_items` row per eval line — the stable
    /// `item_id` Phase 3's override surface keys off (MPE-184).
    private static IReadOnlyList<FeeItemRecord> Items(
        string evalId, FeeEvaluation result)
        => result.Items.Select(i => new FeeItemRecord(
            $"item-{Guid.NewGuid():N}", evalId, i.FeeCode, i.Label,
            i.Section, (double)i.Base,
            System.Text.Json.JsonSerializer.Serialize(
                i.Adjustments, FeeWire.Json),
            (double)i.Total, null)).ToList();

    private static TimeSpan Backoff(long attempts)
    {
        var scaled = TimeSpan.FromTicks(
            BaseBackoff.Ticks * (1L << (int)Math.Min(attempts - 1, 10)));
        return scaled > MaxBackoff ? MaxBackoff : scaled;
    }

    private string Now() => clock.GetUtcNow()
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private string NowPlus(TimeSpan delta) => clock.GetUtcNow()
        .Add(delta).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
