using DbUp;

namespace Lola.ControlPlane;

/// DbUp boot runner for the control-plane schema. Scripts are embedded
/// resources under Migrations/ — append-only, NNNN_name.sql, never edit
/// a landed script (ADR-0002). Keyed files migrate through
/// <see cref="ControlKeyedConnectionManager"/> — the key is applied
/// before DbUp's journal read, never after (MPE-276 / Phase 7b).
public static class ControlPlaneMigrator
{
    public static void Migrate(string connectionString)
        => Migrate(connectionString, null, null);

    public static void Migrate(string connectionString, string? dbPath, string? fileKeyHex)
    {
        if (fileKeyHex is null)
        {
            var plain = DeployChanges.To
                .SqliteDatabase(connectionString)
                .WithScriptsEmbeddedInAssembly(typeof(ControlPlaneMigrator).Assembly)
                .LogToConsole()
                .Build()
                .PerformUpgrade();

            if (!plain.Successful)
                throw new InvalidOperationException("Control-plane migration failed.", plain.Error);
            return;
        }

        var builder = DeployChanges.To
            .SqliteDatabase("Data Source=:memory:") // conn replaced below
            .WithScriptsEmbeddedInAssembly(typeof(ControlPlaneMigrator).Assembly)
            .LogToConsole();
        builder.Configure(c => c.ConnectionManager =
            new ControlKeyedConnectionManager(
                dbPath ?? throw new ArgumentNullException(nameof(dbPath)), fileKeyHex));
        var result = builder.Build().PerformUpgrade();

        if (!result.Successful)
            throw new InvalidOperationException("Control-plane migration failed.", result.Error);
    }
}
