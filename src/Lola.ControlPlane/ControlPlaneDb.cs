using Dapper;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Lola.ControlPlane;

/// Control-plane SQLite store (tenant registry). Every connection goes
/// through <see cref="Open"/> so the required pragmas are always applied:
/// secure_delete + auto_vacuum=FULL keep deleted rows out of freelist
/// pages (data hygiene for a PII-bearing store); WAL and foreign_keys
/// are the baseline set. Per-tenant encrypted DBs land in M4 — the
/// e_sqlcipher engine is wired now so PRAGMA key is a config addition,
/// not a dependency change.
public sealed class ControlPlaneDb
{
    public const string DefaultPath = "data/control.db";

    static ControlPlaneDb() => Batteries_V2.Init();

    private readonly string _connectionString;

    public ControlPlaneDb(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    public string ConnectionString => _connectionString;

    public static string ResolvePath(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultPath : configured;

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        conn.Execute("""
            PRAGMA secure_delete=ON;
            PRAGMA auto_vacuum=FULL;
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            """);
        return conn;
    }

    public bool Healthy()
    {
        using var conn = Open();
        return conn.ExecuteScalar<int>("SELECT 1") == 1;
    }

    public void Migrate() => ControlPlaneMigrator.Migrate(_connectionString);
}
