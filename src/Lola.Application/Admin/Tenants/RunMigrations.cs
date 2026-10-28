using Lola.Application.Identity;

namespace Lola.Application.Admin.Tenants;

public sealed record RunMigrationsCommand;

public sealed record MigrationRunResult(
    IReadOnlyList<TenantMigrationResult> Results, int Applied, int Failed);

/// Fleet-wide tenant migration rollout (MPE-93): runs every active
/// tenant file to the latest embedded migration, isolating failures
/// per tenant — a broken file is reported, not fatal to the fleet.
public static class RunMigrationsHandler
{
    public static async Task<MigrationRunResult> Handle(
        RunMigrationsCommand _, ITenantMigrationRunner runner, CancellationToken ct)
    {
        var results = await runner.MigrateAllAsync(ct);
        return new MigrationRunResult(results,
            results.Count(r => r.Success),
            results.Count(r => !r.Success));
    }
}
