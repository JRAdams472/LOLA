using Lola.Application.Identity;
using Lola.Application.Pricing;
using Lola.Application.Tenancy;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Lola.Api.Pricing;

/// Registry-scoped outbox drain (MPE-130) — one pass over every
/// active tenant file, executing due `pricing_outbox` rows against
/// the configured adapter. Like TenantMigrationRunner this is a job
/// surface: the tenant comes from the registry record, not a request,
/// so each drain opens an explicit TenantContext scope (same shape
/// middleware uses) before any data-plane touch.
///
/// Retry policy: a row gets MaxAttempts executions with exponential
/// backoff (5s × 2ⁿ, capped at 5m); terminal attempts and
/// non-retryable adapter errors land in the DLQ and fail the eval.
public sealed class PricingOutboxProcessor(
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
            .GetRequiredService<IPricingRepository>();
        var adapter = scope.ServiceProvider
            .GetRequiredService<IPricingService>();
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

    private async Task ProcessRowAsync(IPricingRepository repo,
        IPricingService adapter, PricingOutboxRow row,
        CancellationToken ct)
    {
        var attempts = row.Attempts + 1;
        try
        {
            var payload = PricingWire.ParsePayload(row.PayloadJson)
                ?? throw new PricingAdapterException(
                    PricingErrorKind.Contract,
                    "unparseable outbox payload");
            var result = await adapter.EvaluateAsync(
                payload.Scenario, payload.Options, ct);
            await repo.CompleteAsync(row.OutboxId, row.EvalId,
                adapter.Adapter, PricingWire.ResultJson(result), Now(), ct);
        }
        catch (PricingAdapterException ex)
            when (!ex.Retryable || attempts >= MaxAttempts)
        {
            await repo.DeadLetterAsync(row.OutboxId, row.EvalId,
                attempts, $"{ex.Kind}: {ex.Message}", ct);
        }
        catch (PricingAdapterException ex)
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
