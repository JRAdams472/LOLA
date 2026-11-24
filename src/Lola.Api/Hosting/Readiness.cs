using Lola.ControlPlane;
using Lola.Infrastructure.OpenBao;
using Lola.Infrastructure.Vault;
using Microsoft.Extensions.DependencyInjection;

namespace Lola.Api.Hosting;

/// Dependency readiness for /readyz (MPE-277) — distinct from
/// /healthz liveness. Each enabled backing component reports a
/// sanitized status string; any required piece down → 503. Names and
/// statuses only — never credentials, paths, or borrower data.
public static class Readiness
{
    public static async Task<IResult> CheckAsync(IServiceProvider sp,
        bool isProduction, CancellationToken ct)
    {
        var checks = new Dictionary<string, string>();

        var db = sp.GetRequiredService<ControlPlaneDb>();
        var dbOk = db.Healthy();
        checks["controlDb"] = dbOk ? "ok" : "down";
        // Migration currency is meaningless when the file can't open —
        // report "down" rather than a misleading "behind".
        var migrationsOk = dbOk && db.MigrationsCurrent();
        checks["migrations"] = !dbOk ? "down" : migrationsOk ? "current" : "behind";

        var seal = await sp.GetRequiredService<OpenBaoTransitClient>()
            .SealProbeAsync(ct);
        checks["openbao"] = seal;
        // Outside Production an unconfigured bao is a tolerated dev
        // shape; in Production the profile validator has already made
        // "unconfigured" impossible.
        var baoOk = seal == "unsealed"
            || (!isProduction && seal == "unconfigured");

        // The scanner is registered only when VAULT_ADAPTER=minio —
        // a disabled or stub vault has no scanner to probe.
        var scannerOk = true;
        if (sp.GetService<ClamAvScanner>() is { } clam)
        {
            scannerOk = await clam.PingAsync(ct);
            checks["scanner"] = scannerOk ? "ok" : "down";
        }

        var ready = dbOk && migrationsOk && baoOk && scannerOk;
        var body = new Dictionary<string, object?>
        {
            ["status"] = ready ? "ready" : "not-ready",
            ["checks"] = checks,
        };
        return ready
            ? Results.Json(body)
            : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
