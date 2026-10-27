using DbUp;

namespace Lola.ControlPlane;

/// DbUp boot runner for the control-plane schema. Scripts are embedded
/// resources under Migrations/ — append-only, NNNN_name.sql, never edit
/// a landed script (ADR-0002).
public static class ControlPlaneMigrator
{
    public static void Migrate(string connectionString)
    {
        var result = DeployChanges.To
            .SqliteDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(ControlPlaneMigrator).Assembly)
            .LogToConsole()
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
            throw new InvalidOperationException("Control-plane migration failed.", result.Error);
    }
}
