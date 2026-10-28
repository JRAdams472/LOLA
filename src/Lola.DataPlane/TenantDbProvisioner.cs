using System.Security.Cryptography;
using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.Domain.Identity;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Lola.DataPlane;

/// Creates a tenant's SQLCipher data-plane file. The file key is a
/// fresh random 256-bit key wrapped by the tenant's transit key; only
/// the `vault:` ciphertext returns to the caller for the registry. The
/// plaintext key exists in memory just long enough to create the file.
/// Schema lives in migrations (Migrations/*.sql applied by
/// TenantMigrationRunner at the pipeline's `migrate` step) — this
/// creates only the empty keyed file.
public sealed class TenantDbProvisioner(IFieldCipher cipher) : ITenantDbProvisioner
{
    // Explicit provider — see ControlPlaneDb: plain e_sqlite3 and
    // e_sqlcipher can coexist in the graph; this forces the cipher.
    static TenantDbProvisioner() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    public async Task<string> ProvisionFileAsync(TenantRecord tenant, CancellationToken ct = default)
    {
        var fileKey = RandomNumberGenerator.GetBytes(32);
        var fileKeyHex = Convert.ToHexString(fileKey);

        // Wrap under the tenant's transit key — the registry only ever
        // stores this ciphertext.
        string wrapped;
        using (CipherContext.Use(tenant.Id))
        {
            wrapped = await cipher.EncryptAsync(
                Convert.ToBase64String(fileKey), ct);
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(tenant.DbPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // PRAGMA key MUST be the first statement on a SQLCipher
        // connection — before any hygiene pragma or DDL. Pooling off:
        // the provisioned connection must die with this method so no
        // keyed handle lingers in the pool.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = tenant.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using (var conn = new SqliteConnection(connectionString))
        {
            conn.Open();
            conn.Execute($"""
                PRAGMA key = "x'{fileKeyHex}'";
                PRAGMA secure_delete=ON;
                PRAGMA auto_vacuum=FULL;
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                """);
        }

        return wrapped;
    }
}
