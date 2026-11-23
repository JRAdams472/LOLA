using System.Data;
using Dapper;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using Microsoft.Data.Sqlite;

namespace Lola.ControlPlane;

/// DbUp connection manager that opens KEYED SQLCipher connections —
/// PRAGMA key first, then the required hygiene pragmas, same discipline
/// as <see cref="ControlPlaneDb.Open"/>. DbUp's stock sqlite connection
/// manager has no key hook (see KeyedSqliteConnectionManager in the
/// data plane, MPE-93).
public sealed class ControlKeyedConnectionManager : DatabaseConnectionManager
{
    public ControlKeyedConnectionManager(string dbPath, string fileKeyHex)
        : base(_ => OpenKeyed(dbPath, fileKeyHex))
    {
    }

    /// SQLite executes multi-statement batches natively — hand the
    /// script through unsplit rather than a GO-style splitter.
    public override IEnumerable<string> SplitScriptIntoCommands(string scriptContents)
        => [scriptContents];

    /// Keyed open shared with job surfaces that open control-plane
    /// files outside DbUp (conversion tooling, backups, tests) — same
    /// PRAGMA order and hygiene pragmas as every control-plane
    /// connection.
    public static IDbConnection OpenKeyed(string dbPath, string fileKeyHex)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString());
        conn.Open();
        conn.Execute($"""
            PRAGMA key = "x'{fileKeyHex}'";
            PRAGMA secure_delete=ON;
            PRAGMA auto_vacuum=FULL;
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            """);
        return conn;
    }
}
