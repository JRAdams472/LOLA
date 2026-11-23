using Dapper;
using Lola.ControlPlane;
using Microsoft.Data.Sqlite;
using Shouldly;

namespace Lola.ControlPlane.Tests;

public class ControlPlaneDbTests
{
    private const string Key = TestDbKeys.ControlFileKeyHex;
    private const string WrongKey =
        "FFEEFFEEFFEEFFEEFFEEFFEEFFEEFFEEFFEEFFEEFFEEFFEEFFEEFFEEFFEEFFEE";

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "lola-tests", $"cp-{Guid.NewGuid():N}.db");

    private static ControlPlaneDb TempDb()
        => new(TempPath(), Key);

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
    public void Healthy_returns_true_on_fresh_keyed_db()
        => TempDb().Healthy().ShouldBeTrue();

    [Fact]
    public void Unkeyed_open_of_keyed_file_fails()
    {
        var path = TempPath();
        var keyed = new ControlPlaneDb(path, Key);
        keyed.Migrate();

        var unkeyed = new ControlPlaneDb(path, null);
        unkeyed.Healthy().ShouldBeFalse();
        Should.Throw<SqliteException>(() =>
        {
            using var conn = unkeyed.Open();
            _ = conn.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master");
        });
    }

    [Fact]
    public void Wrong_key_open_fails_at_open()
    {
        var path = TempPath();
        new ControlPlaneDb(path, Key).Migrate();

        var wrong = new ControlPlaneDb(path, WrongKey);
        wrong.Healthy().ShouldBeFalse();
        Should.Throw<SqliteException>(() => wrong.Open());
    }

    [Fact]
    public void Keyed_migration_runs_and_idx_columns_exist()
    {
        var path = TempPath();
        var db = new ControlPlaneDb(path, Key);
        db.Migrate();

        using var conn = db.Open();
        conn.ExecuteScalar<int>(
            "SELECT count(*) FROM pragma_table_info('users') WHERE name='email_idx'")
            .ShouldBe(1);
        conn.ExecuteScalar<int>(
            "SELECT count(*) FROM pragma_table_info('tenants') WHERE name='slug_idx'")
            .ShouldBe(1);
    }

    [Fact]
    public void Keyed_file_holds_no_marker_plaintext()
    {
        const string marker = "marker-mpe276@example.test";
        var path = TempPath();
        var db = new ControlPlaneDb(path, Key);
        db.Migrate();
        using (var conn = db.Open())
        {
            conn.Execute("""
                INSERT INTO tenants (id, slug, display_name, status, db_path)
                VALUES ('t-marker', 'mpe276-marker-slug', @marker, 'active', 'x.db')
                """, new { marker });
        }

        // Scan the DB image and (if present) the WAL — both must be
        // ciphertext; plaintext pages in either artifact fail.
        foreach (var artifact in new[] { path, $"{path}-wal", $"{path}-shm" })
        {
            if (!File.Exists(artifact)) continue;
            var haystack = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(artifact));
            haystack.ShouldNotContain(marker);
            haystack.ShouldNotContain("mpe276-marker-slug");
            if (artifact == path)
                haystack.ShouldNotContain("tenants"); // schema text is ciphertext too
        }

        // And the row is still readable through the keyed path.
        using var check = db.Open();
        check.ExecuteScalar<string>(
            "SELECT display_name FROM tenants WHERE id='t-marker'")
            .ShouldBe(marker);
    }
}

public class ControlPlaneDbPlaintextGuardTests
{
    [Fact]
    public void Keyed_open_of_existing_plaintext_file_fails_clear()
    {
        var path = Path.Combine(Path.GetTempPath(), "lola-tests",
            $"cp-{Guid.NewGuid():N}.db");
        // Create a plaintext file the unkeyed way — the legacy shape.
        var legacy = new ControlPlaneDb(path, null);
        legacy.Migrate();
        using (var conn = legacy.Open())
            conn.Execute(
                "INSERT INTO tenants (id, slug, display_name, status, db_path) " +
                "VALUES ('t1', 't1', 'T', 'active', 'x.db')");

        var ex = Should.Throw<InvalidOperationException>(() =>
            new ControlPlaneDb(path, TestDbKeys.ControlFileKeyHex).Open());
        ex.Message.ShouldContain("unkeyed");
        ex.Message.ShouldContain("cpconvert");
    }
}
