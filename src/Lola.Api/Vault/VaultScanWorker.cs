using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lola.Api.Vault;

/// Hosted poll loop over <see cref="VaultScanProcessor"/> (MPE-201).
/// Interval comes from VAULT_SCAN_INTERVAL_MS (default 2s); the
/// whole hosted service is off when VAULT_SCAN_ENABLED=false (tests
/// drain the processor directly).
public sealed class VaultScanWorker(
    VaultScanProcessor processor, IConfiguration config,
    ILogger<VaultScanWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(
            int.TryParse(config["VAULT_SCAN_INTERVAL_MS"], out var ms)
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
                log.LogWarning(ex, "vault scan drain failed");
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
