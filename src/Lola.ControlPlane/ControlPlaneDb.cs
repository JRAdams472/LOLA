using Dapper;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Lola.ControlPlane;

/// Control-plane SQLite store (tenant registry). Every connection goes
/// through <see cref="Open"/> so the required pragmas are always applied:
/// secure_delete + auto_vacuum=FULL keep deleted rows out of freelist
/// pages (data hygiene for a PII-bearing store); WAL and foreign_keys
/// are the baseline set.
///
/// MPE-276 / Phase 7b: the file may be SQLCipher-keyed. Callers pass
/// <paramref name="fileKeyHex"/> explicitly — `null` means an
/// intentionally UNKEYED open (transitional dev file or the offline
/// conversion source), never a silent fallback. Keyed opens apply
/// `PRAGMA key` as the first statement and force a page-1 read so a
/// wrong or missing key throws here instead of inside a request.
public sealed class ControlPlaneDb
{
    public const string DefaultPath = "data/control.db";

    // Explicit provider: BOTH bundle_e_sqlite3 (plain) and bundle_e_
    // sqlcipher can ride the package graph (e.g. Microsoft.Data.Sqlite
    // pulls the plain one); Batteries_V2.Init() would bind whichever
    // batteries assembly wins NuGet resolution — silently. SetProvider
    // makes the SQLCipher engine deterministic.
    static ControlPlaneDb() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly string? _fileKeyHex;

    public ControlPlaneDb(string dbPath, string? fileKeyHex)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _dbPath = dbPath;
        // Pooling off — keyed handles must not linger in a shared pool;
        // matches the data-plane discipline.
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = dbPath, Pooling = false }.ToString();
        _fileKeyHex = fileKeyHex;
    }

    public string ConnectionString => _connectionString;

    public static string ResolvePath(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultPath : configured;

    public SqliteConnection Open()
    {
        // A key configured against an existing PLAINTEXT file must fail
        // with an actionable error, not a lazy "file is not a database"
        // — plaintext files convert via the offline tool (Phase 7d),
        // never silently. SQLite's magic header identifies them.
        if (_fileKeyHex is not null && File.Exists(_dbPath) &&
            new FileInfo(_dbPath).Length > 0)
        {
            Span<byte> header = stackalloc byte[16];
            using (var fs = new FileStream(
                _dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                _ = fs.Read(header);
            if (header.SequenceEqual("SQLite format 3\0"u8))
                throw new InvalidOperationException(
                    $"Control-plane file '{_dbPath}' is unkeyed but a " +
                    "CONTROL_DB_KEY is configured — convert it offline " +
                    "(tools/cpconvert) or remove it for a fresh keyed " +
                    "bootstrap. It is never opened plaintext silently.");
        }

        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // PRAGMA key MUST be the first statement on a SQLCipher
        // connection — before any hygiene pragma or DDL.
        if (_fileKeyHex is not null)
            conn.Execute($"PRAGMA key = \"x'{_fileKeyHex}'\";");
        conn.Execute("""
            PRAGMA secure_delete=ON;
            PRAGMA auto_vacuum=FULL;
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            """);
        // Force a page-1 decrypt now so a wrong key (or a key applied
        // to an unkeyed legacy file) fails at Open — the cipher checks
        // keys lazily on first read otherwise.
        if (_fileKeyHex is not null)
            _ = conn.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master");
        return conn;
    }

    public bool Healthy()
    {
        try
        {
            using var conn = Open();
            // A bare SELECT 1 never touches the file — read the schema
            // page so a missing/wrong key reports not-ready.
            return conn.ExecuteScalar<int>("SELECT count(*) FROM sqlite_master") >= 0;
        }
        catch (Exception)
        {
            // Missing/wrong key, locked or unreadable file — report
            // not-ready rather than surfacing store internals.
            return false;
        }
    }

    /// Readiness probe (MPE-277): true when the DbUp journal holds at
    /// least every script the assembly ships — a file behind the schema
    /// is not-ready, never silently tolerated. Throws → false.
    public bool MigrationsCurrent()
    {
        try
        {
            using var conn = Open();
            return conn.ExecuteScalar<int>(
                "SELECT count(*) FROM SchemaVersions")
                >= ControlPlaneMigrator.ExpectedScriptCount;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Migrate() => ControlPlaneMigrator.Migrate(_connectionString, _dbPath, _fileKeyHex);
}
