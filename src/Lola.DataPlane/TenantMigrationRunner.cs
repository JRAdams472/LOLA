using DbUp;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Identity;
using SQLitePCL;

namespace Lola.DataPlane;

/// Rolls embedded tenant-file migrations via DbUp inside keyed
/// SQLCipher connections (MPE-93). Job surface — takes an explicit
/// TenantRecord; unwraps the file key under CipherContext for the
/// tenant rather than relying on request-scoped TenantContext.
/// Per-tenant failures are isolated: MigrateAll records them and
/// continues the fleet rollout.
public sealed class TenantMigrationRunner(
    IFieldCipher cipher,
    ITenantRegistry registry,
    IMigrationTracker tracker) : ITenantMigrationRunner
{
    // Explicit provider — see TenantConnectionFactory.
    static TenantMigrationRunner() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    public async Task<TenantMigrationResult> MigrateAsync(
        TenantRecord tenant, CancellationToken ct = default)
    {
        try
        {
            var keyHex = await UnwrapKeyHexAsync(tenant, ct);
            var builder = DeployChanges.To
                .SqliteDatabase("Data Source=:memory:") // conn replaced below
                .WithScriptsEmbeddedInAssembly(typeof(TenantMigrationRunner).Assembly)
                .LogToConsole();
            builder.Configure(c => c.ConnectionManager =
                new KeyedSqliteConnectionManager(tenant.DbPath, keyHex));
            var result = builder.Build().PerformUpgrade();

            if (result.Successful)
            {
                var names = result.Scripts.Select(s => s.Name).ToList();
                foreach (var n in names)
                    await tracker.RecordAsync(tenant.Id, VersionOf(n), n, "applied", null, ct);
                return new TenantMigrationResult(tenant.Id, true, names, null);
            }

            var err = result.Error.Message;
            await tracker.RecordAsync(tenant.Id, 0, "(failed-run)", "failed", err, ct);
            return new TenantMigrationResult(tenant.Id, false, [], err);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await tracker.RecordAsync(tenant.Id, 0, "(failed-run)", "failed", ex.Message, ct);
            return new TenantMigrationResult(tenant.Id, false, [], ex.Message);
        }
    }

    public async Task<IReadOnlyList<TenantMigrationResult>> MigrateAllAsync(
        CancellationToken ct = default)
    {
        var results = new List<TenantMigrationResult>();
        foreach (var tenant in await registry.ListAsync(ct))
        {
            if (tenant.Status == "retired" || tenant.DbKeyWrapped is null) continue;
            results.Add(await MigrateAsync(tenant, ct));
        }
        return results;
    }

    private async Task<string> UnwrapKeyHexAsync(TenantRecord tenant, CancellationToken ct)
    {
        var wrapped = tenant.DbKeyWrapped ?? throw new InvalidOperationException(
            $"Tenant '{tenant.Id}' has no data-plane key — provisioning is incomplete.");
        using (CipherContext.Use(tenant.Id))
        {
            var b64 = await cipher.DecryptAsync(wrapped, ct);
            return Convert.ToHexString(Convert.FromBase64String(b64));
        }
    }

    /// Extract NNNN from an embedded script name like
    /// `Lola.DataPlane.Migrations.0001_baseline.sql`.
    private static int VersionOf(string scriptName)
    {
        var file = scriptName.Split('.')[^2];
        return int.TryParse(file.Split('_')[0], out var v) ? v : 0;
    }
}
