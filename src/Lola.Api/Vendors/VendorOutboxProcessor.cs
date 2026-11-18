using Lola.Application.Applications;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.Application.Vendors;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Lola.Api.Vendors;

/// Registry-scoped vendor outbox drain (MPE-227) — submits due
/// `vendor_outbox` rows through the configured adapter per tenant.
/// Same discipline as the fee/doc drains: explicit TenantContext
/// from the registry record, fresh DI scope per tenant, bounded
/// attempts, exponential backoff, DLQ on terminal failure.
///
/// After a successful submit the row is polled once opportunistically
/// — a vendor answering synchronously (the stub) completes the whole
/// lifecycle in one drain; a null poll leaves the order `submitted`
/// for the webhook/poll channels (MPE-228). Terminal adapter
/// failures fail the order AND write the service-step event.
public sealed class VendorOutboxProcessor(
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
            .GetRequiredService<IVendorOrderRepository>();
        var adapter = scope.ServiceProvider
            .GetRequiredService<IVendorAdapter>();
        var creds = scope.ServiceProvider
            .GetRequiredService<IVendorCredentialSource>();
        var cipher = scope.ServiceProvider
            .GetRequiredService<IFieldCipher>();
        var processed = 0;
        using (TenantContext.Use(TenantScope.From(tenant)))
        {
            var rows = await repo.ListDueOutboxAsync(
                Now(), BatchSize, ct);
            ct.ThrowIfCancellationRequested();
            await Task.WhenAll(rows.Select(row => ProcessRowAsync(
                repo, adapter, creds, cipher, row, ct)));
            processed += rows.Count;
        }
        return processed;
    }

    private async Task ProcessRowAsync(IVendorOrderRepository repo,
        IVendorAdapter adapter, IVendorCredentialSource creds,
        IFieldCipher cipher, VendorOutboxRow row, CancellationToken ct)
    {
        var attempts = row.Attempts + 1;
        try
        {
            var order = await repo.GetAsync(row.OrderId, ct)
                ?? throw new VendorAdapterException(
                    VendorErrorKind.Contract,
                    "outbox row references a missing order");
            var kind = VendorGuards.Parse(order.Kind);
            var request = (VendorOrderRequest?)
                System.Text.Json.JsonSerializer.Deserialize(
                    await cipher.DecryptAsync(order.RequestJson, ct),
                    RequestType(kind), VendorRequestMapper.Json)
                ?? throw new VendorAdapterException(
                    VendorErrorKind.Contract,
                    "unparseable order payload");
            var credentials = await creds.ForAsync(kind, ct);
            var receipt = await adapter.SubmitAsync(
                request, credentials, ct);
            await repo.MarkSubmittedAsync(
                row.OrderId, receipt.ExternalId, row.OutboxId, ct);

            // Opportunistic completion — vendors answering in-band
            // finish here; push vendors close via webhook (MPE-228).
            var result = await adapter.PollAsync(
                receipt, credentials, ct);
            if (result is not null)
            {
                var resultJson = await cipher.EncryptAsync(
                    VendorRequestMapper.ResultJson(result), ct);
                await repo.CompleteAsync(row.OrderId,
                    order.ApplicationId, resultJson, row.OutboxId,
                    result.Outcome,
                    WorkflowPipeline.ServiceStep(kind),
                    order.OrderedBy, ct);
            }
        }
        catch (VendorAdapterException ex)
            when (!ex.Retryable || attempts >= MaxAttempts)
        {
            await Fail(repo, row, attempts,
                $"{ex.Kind}: {ex.Message}", ct);
        }
        catch (VendorAdapterException ex)
        {
            await repo.MarkOutboxRetryAsync(row.OutboxId, attempts,
                NowPlus(Backoff(attempts)),
                $"{ex.Kind}: {ex.Message}", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (attempts >= MaxAttempts)
                await Fail(repo, row, attempts,
                    $"unexpected: {ex.Message}", ct);
            else
                await repo.MarkOutboxRetryAsync(row.OutboxId, attempts,
                    NowPlus(Backoff(attempts)),
                    $"unexpected: {ex.Message}", ct);
        }
    }

    private async Task Fail(IVendorOrderRepository repo,
        VendorOutboxRow row, long attempts, string error,
        CancellationToken ct)
    {
        var order = await repo.GetAsync(row.OrderId, ct);
        if (order is not null)
            await repo.FailAsync(row.OrderId, order.ApplicationId,
                error, row.OutboxId, attempts,
                WorkflowPipeline.ServiceStep(
                    VendorGuards.Parse(order.Kind)),
                order.OrderedBy, ct);
        else
            await repo.MarkOutboxDlqAsync(row.OutboxId, attempts,
                error, ct);
    }

    private static Type RequestType(VendorKind kind) => kind switch
    {
        VendorKind.Credit => typeof(CreditOrderRequest),
        VendorKind.Voe => typeof(VoeOrderRequest),
        VendorKind.Voi => typeof(VoiOrderRequest),
        VendorKind.Flood => typeof(FloodOrderRequest),
        VendorKind.Fraud => typeof(FraudOrderRequest),
        VendorKind.Aus => typeof(AusOrderRequest),
        _ => typeof(VendorOrderRequest)
    };

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
