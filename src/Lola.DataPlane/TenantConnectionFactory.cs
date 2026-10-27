using Dapper;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Lola.DataPlane;

/// Opens keyed SQLCipher connections to the CURRENT request's tenant
/// file — and only that file. There is deliberately no tenant-id
/// parameter on this type: the tenant comes from <see cref="TenantContext"/>,
/// set once per request by resolution middleware, so no code path can
/// name another tenant's file (invariant: tenant isolation).
///
/// The wrapped file key is unwrapped once per factory instance (i.e.
/// once per request) and held in memory for the request's lifetime;
/// it never rests anywhere but OpenBao ciphertext in the registry.
public sealed class TenantConnectionFactory(IFieldCipher cipher)
{
    // Explicit provider — see ControlPlaneDb.
    static TenantConnectionFactory() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private string? _fileKeyHex;

    private async Task<string> FileKeyHexAsync(CancellationToken ct)
    {
        if (_fileKeyHex is not null) return _fileKeyHex;
        var scope = TenantContext.Required;
        var wrapped = scope.DbKeyWrapped ?? throw new InvalidOperationException(
            $"Tenant '{scope.TenantId}' has no data-plane key — provisioning is incomplete.");
        // The transit cipher needs the tenant in CipherContext — scope
        // it ourselves so the factory works even when middleware only
        // set TenantContext (e.g. tests, provisioning paths).
        using (CipherContext.Use(scope.TenantId))
        {
            var b64 = await cipher.DecryptAsync(wrapped, ct);
            _fileKeyHex = Convert.ToHexString(Convert.FromBase64String(b64));
        }
        return _fileKeyHex;
    }

    /// Open a keyed connection to the resolved tenant's file with the
    /// required hygiene pragmas (invariant 10). PRAGMA key is always
    /// the first statement. Pooling is OFF deliberately — a pooled
    /// physical connection would hold the tenant file handle AND its
    /// plaintext key across requests; M4 may revisit pooling with an
    /// explicit key-lifecycle design.
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var scope = TenantContext.Required;
        var keyHex = await FileKeyHexAsync(ct);
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = scope.DbPath,
            Pooling = false
        }.ToString());
        conn.Open();
        conn.Execute($"""
            PRAGMA key = "x'{keyHex}'";
            PRAGMA secure_delete=ON;
            PRAGMA auto_vacuum=FULL;
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            """);
        return conn;
    }
}
