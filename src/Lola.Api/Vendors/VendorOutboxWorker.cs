using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lola.Api.Vendors;

/// Hosted poll loop over <see cref="VendorOutboxProcessor"/>
/// (MPE-227). Interval from VENDOR_OUTBOX_INTERVAL_MS (default 2s);
/// the service is off when VENDOR_OUTBOX_ENABLED=false — tests drain
/// the processor directly, mirroring the fee worker contract.
public sealed class VendorOutboxWorker(
    VendorOutboxProcessor processor, IConfiguration config,
    ILogger<VendorOutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(
            int.TryParse(config["VENDOR_OUTBOX_INTERVAL_MS"], out var ms)
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
                log.LogWarning(ex, "vendor outbox drain failed");
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
