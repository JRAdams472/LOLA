using Lola.Application.Documents;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Lola.Api.Documents;

/// Registry-scoped doc-outbox drain (MPE-190) — one pass over every
/// active tenant file, executing due `doc_outbox` rows against the
/// configured adapter. Two-phase per row: no vendor request id yet →
/// `GenerateAsync` (idempotent on `Idempotency-Key`); request id
/// stamped → `GetStatusAsync` poll until terminal.
///
/// Attempts count EXECUTIONS, not just errors — a still-rendering
/// package polls on a fixed cadence without consuming error budget,
/// but an exhausted row (MaxAttempts executions) DLQs so a
/// permanently-stuck package can't drain forever.
public sealed class DocOutboxProcessor(
    IServiceProvider services, ITenantRegistry registry,
    TimeProvider clock,
    ILogger<DocOutboxProcessor> log)
{
    private const int BatchSize = 25;
    private const int MaxAttempts = 20;
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(5);
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
        await using var scope = services.CreateAsyncScope();
        var repo = scope.ServiceProvider
            .GetRequiredService<IDocumentRepository>();
        var adapter = scope.ServiceProvider
            .GetRequiredService<IDocumentService>();
        var processed = 0;
        // TenantContext scopes the file; CipherContext scopes the
        // transit decrypts (payload_json unwrap + vendor key).
        using (TenantContext.Use(TenantScope.From(tenant)))
        using (CipherContext.Use(tenant.Id))
        {
            var rows = await repo.ListDueOutboxAsync(
                Now(), BatchSize, ct);
            ct.ThrowIfCancellationRequested();
            await Task.WhenAll(rows.Select(
                row => ProcessRowAsync(scope.ServiceProvider,
                    repo, adapter, row, ct)));
            processed += rows.Count;
        }
        return processed;
    }

    private async Task ProcessRowAsync(IServiceProvider sp,
        IDocumentRepository repo, IDocumentService adapter,
        DocOutboxRow row, CancellationToken ct)
    {
        var attempts = row.Attempts + 1;
        try
        {
            if (attempts > MaxAttempts)
            {
                await repo.DeadLetterAsync(row.OutboxId, row.DocId,
                    row.Attempts, "generation timed out", ct);
                return;
            }

            DocGeneration gen;
            if (row.RequestId is null)
            {
                var request = DocWire.ParsePayload(row.PayloadJson)
                    ?? throw DocAdapterException.Contract(
                        "unparseable outbox payload");
                gen = await adapter.GenerateAsync(request, ct);
            }
            else
            {
                gen = await adapter.GetStatusAsync(row.RequestId, ct);
            }

            switch (gen.Status)
            {
                case "completed":
                    await repo.CompleteAsync(row.OutboxId, row.DocId,
                        gen.RequestId, gen.Filename, Now(), ct);
                    // TRID tolerance (M21): a completed disclosure
                    // version diffs against its baseline — violations
                    // surface as events + a blocking cure condition.
                    // `doc.Kind` is the package kind; `row.Kind` is the
                    // outbox job kind ("generate").
                    var doc = await repo.GetDocAsync(row.DocId, ct);
                    if (doc is not null &&
                        Lola.Application.Compliance.DisclosureKind
                            .Of(doc.Kind) is not null)
                    {
                        await Lola.Application.Compliance
                        .EvaluateDisclosureTolerance.Handle(
                            doc.ApplicationId, row.DocId,
                            "system:doc-drain",
                            sp.GetRequiredService<Lola.Application
                                .Compliance.IToleranceRepository>(),
                            sp.GetRequiredService<Lola.Application
                                .Fees.IFeeRepository>(),
                            sp.GetRequiredService<Lola.Application
                                .Events.IConditionRepository>(),
                            sp.GetRequiredService<Lola.Application
                                .Events.ILoanEventLog>(), ct);
                    }
                    break;
                case "failed":
                    await repo.DeadLetterAsync(row.OutboxId, row.DocId,
                        attempts, gen.Error ?? "vendor reported failure",
                        ct);
                    break;
                default: // generating — poll again shortly
                    if (row.RequestId is null)
                        await repo.MarkGeneratingAsync(row.OutboxId,
                            row.DocId, gen.RequestId,
                            NowPlus(PollInterval), ct);
                    else
                        await repo.RescheduleAsync(row.OutboxId,
                            row.DocId, attempts, NowPlus(PollInterval),
                            "generation in progress", ct);
                    break;
            }
        }
        catch (DocAdapterException ex)
            when (!ex.Retryable || attempts >= MaxAttempts)
        {
            await repo.DeadLetterAsync(row.OutboxId, row.DocId,
                attempts, $"{ex.Kind}: {ex.Message}", ct);
        }
        catch (DocAdapterException ex)
        {
            await repo.RescheduleAsync(row.OutboxId, row.DocId,
                attempts, NowPlus(Backoff(attempts)),
                $"{ex.Kind}: {ex.Message}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex,
                "doc outbox row {OutboxId} reschedules after unexpected error",
                row.OutboxId);
            if (attempts >= MaxAttempts)
                await repo.DeadLetterAsync(row.OutboxId, row.DocId,
                    attempts, $"unexpected: {ex.Message}", ct);
            else
                await repo.RescheduleAsync(row.OutboxId, row.DocId,
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
