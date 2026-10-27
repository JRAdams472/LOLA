using Dapper;
using Lola.ControlPlane;
using Shouldly;

namespace Lola.ControlPlane.Tests;

public class ControlPlaneDbTests
{
    private static ControlPlaneDb TempDb()
        => new(Path.Combine(Path.GetTempPath(), "lola-tests", $"cp-{Guid.NewGuid():N}.db"));

    [Fact]
    public void Open_applies_required_pragmas()
    {
        using var conn = TempDb().Open();
        conn.ExecuteScalar<string>("PRAGMA journal_mode").ShouldBe("wal");
        conn.ExecuteScalar<long>("PRAGMA foreign_keys").ShouldBe(1);
        conn.ExecuteScalar<long>("PRAGMA secure_delete").ShouldBeGreaterThanOrEqualTo(1);
        conn.ExecuteScalar<long>("PRAGMA auto_vacuum").ShouldBe(1); // 1=FULL (2=INCREMENTAL)
    }

    [Fact]
    public void Healthy_returns_true_on_fresh_db()
        => TempDb().Healthy().ShouldBeTrue();
}
