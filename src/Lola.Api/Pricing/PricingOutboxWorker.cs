using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lola.Api.Pricing;

/// Hosted poll loop over <see cref="PricingOutboxProcessor"/>
/// (MPE-130). Interval comes from PRICING_OUTBOX_INTERVAL_MS
/// (default 2s); the whole hosted service is off when
/// PRICING_OUTBOX_ENABLED=false (tests drain the processor directly).
public sealed class PricingOutboxWorker(
    PricingOutboxProcessor processor, IConfiguration config,
    ILogger<PricingOutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(
            int.TryParse(config["PRICING_OUTBOX_INTERVAL_MS"], out var ms)
                ? ms : 2_000);
        using var timer = new PeriodicTimer(interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await processor.DrainAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "pricing outbox drain failed");
            }
            try
            {
                await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
