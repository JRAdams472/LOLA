using System.Data;
using Dapper;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using Microsoft.Data.Sqlite;

namespace Lola.DataPlane;

/// DbUp connection manager that opens KEYED SQLCipher connections —
/// PRAGMA key first, then the required hygiene pragmas, same discipline
/// as TenantConnectionFactory. DbUp otherwise can't open a tenant file:
/// its stock SqliteConnectionManager has no key hook (MPE-93).
public sealed class KeyedSqliteConnectionManager : DatabaseConnectionManager
{
    public KeyedSqliteConnectionManager(string dbPath, string fileKeyHex)
        : base(_ => OpenKeyed(dbPath, fileKeyHex))
    {
    }

    /// SQLite executes multi-statement batches natively — hand the
    /// script through unsplit rather than a GO-style splitter.
    public override IEnumerable<string> SplitScriptIntoCommands(string scriptContents)
        => [scriptContents];

    private static IDbConnection OpenKeyed(string dbPath, string fileKeyHex)
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
