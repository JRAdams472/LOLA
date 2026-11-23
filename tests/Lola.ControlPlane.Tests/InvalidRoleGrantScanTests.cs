using Dapper;
using Lola.ControlPlane;
using Shouldly;

namespace Lola.ControlPlane.Tests;

/// MPE-271 reconciliation scan: group_roles rows carrying machine
/// authority ('Service') or anything outside the human-assignable set
/// are reported for operator reconciliation — never mutated, never
/// silently broadened. Human roles pass clean.
public class InvalidRoleGrantScanTests
{
    [Fact]
    public void Reports_machine_role_grants_and_leaves_rows_untouched()
    {
        var db = new ControlPlaneDb(Path.Combine(
            Path.GetTempPath(), "lola-tests", $"scan-{Guid.NewGuid():N}.db"), TestDbKeys.ControlFileKeyHex);
        db.Migrate();
        using var conn = db.Open();
        conn.Execute(
            "INSERT INTO tenants (id, slug, display_name, status, db_path) " +
            "VALUES ('t1', 't1', 'T', 'active', 'x.db')");
        conn.Execute(
            "INSERT INTO groups (id, tenant_id, name) VALUES " +
            "('g1', 't1', 'legacy-svc'), ('g2', 't1', 'officers')");
        conn.Execute(
            "INSERT INTO group_roles (group_id, role) VALUES " +
            "('g1', 'Service'), ('g2', 'LoanOfficer'), ('g2', 'TenantAdmin')");

        var grants = InvalidRoleGrantScan.Run(db);

        grants.ShouldHaveSingleItem().ShouldBe(
            new InvalidRoleGrant("t1", "g1", "Service"));
        // Report-only: the grant row survives for reconciliation, the
        // scan does not erase history.
        conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM group_roles WHERE role = 'Service'").ShouldBe(1);
    }

    [Fact]
    public void Clean_registry_reports_nothing()
    {
        var db = new ControlPlaneDb(Path.Combine(
            Path.GetTempPath(), "lola-tests", $"scan-{Guid.NewGuid():N}.db"), TestDbKeys.ControlFileKeyHex);
        db.Migrate();
        InvalidRoleGrantScan.Run(db).ShouldBeEmpty();
    }
}
