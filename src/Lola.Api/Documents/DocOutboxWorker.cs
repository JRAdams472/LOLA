using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lola.Api.Documents;

/// Hosted poll loop over <see cref="DocOutboxProcessor"/> (MPE-190).
/// Interval comes from DOC_OUTBOX_INTERVAL_MS (default 2s); the
/// whole hosted service is off when DOC_OUTBOX_ENABLED=false (tests
/// drain the processor directly).
public sealed class DocOutboxWorker(
    DocOutboxProcessor processor, IConfiguration config,
    ILogger<DocOutboxWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(
            int.TryParse(config["DOC_OUTBOX_INTERVAL_MS"], out var ms)
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
                log.LogWarning(ex, "doc outbox drain failed");
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
