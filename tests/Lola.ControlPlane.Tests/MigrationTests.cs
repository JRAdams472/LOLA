using Dapper;
using Lola.ControlPlane;
using Shouldly;

namespace Lola.ControlPlane.Tests;

public class MigrationTests
{
    [Fact]
    public void Migrate_creates_tenant_registry()
    {
        var db = new ControlPlaneDb(
            Path.Combine(Path.GetTempPath(), "lola-tests", $"mig-{Guid.NewGuid():N}.db"));
        db.Migrate();

        using var conn = db.Open();
        var cols = conn.Query<string>("SELECT name FROM pragma_table_info('tenants')").ToList();
        cols.ShouldContain("id");
        cols.ShouldContain("slug");
        cols.ShouldContain("db_path");
        cols.ShouldContain("status");
    }
}
