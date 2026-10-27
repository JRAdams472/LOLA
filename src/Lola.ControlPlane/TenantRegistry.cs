using Dapper;
using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.ControlPlane;

/// Tenant registry over the control-plane SQLite store — routing and
/// provisioning metadata only (ADR-0003). `db_key_wrapped` is transit
/// ciphertext; the plaintext file key is never written here.
public sealed class TenantRegistry(ControlPlaneDb db) : ITenantRegistry
{
    private const string Columns =
        "id, slug, display_name AS DisplayName, status, db_path AS DbPath, " +
        "db_key_wrapped AS DbKeyWrapped, created_utc AS CreatedUtc";

    public async Task<TenantRecord?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.QuerySingleOrDefaultAsync<TenantRecord>(
            new CommandDefinition($"SELECT {Columns} FROM tenants WHERE id = @id",
                new { id }, cancellationToken: ct));
    }

    public async Task<TenantRecord?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.QuerySingleOrDefaultAsync<TenantRecord>(
            new CommandDefinition($"SELECT {Columns} FROM tenants WHERE slug = @slug",
                new { slug }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<TenantRecord>(
            new CommandDefinition($"SELECT {Columns} FROM tenants ORDER BY created_utc",
                cancellationToken: ct));
        return rows.ToList();
    }

    /// INSERT OR IGNORE keeps the step re-entrant — a provisioning retry
    /// sees the existing row rather than a UNIQUE failure.
    public async Task InsertProvisioningAsync(
        string id, string slug, string displayName, string dbPath, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT OR IGNORE INTO tenants (id, slug, display_name, status, db_path)
            VALUES (@id, @slug, @displayName, 'provisioning', @dbPath)
            """, new { id, slug, displayName, dbPath }, cancellationToken: ct));
    }

    public async Task SetDbKeyAsync(string tenantId, string dbKeyWrapped, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tenants SET db_key_wrapped = @dbKeyWrapped,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @tenantId
            """, new { tenantId, dbKeyWrapped }, cancellationToken: ct));
    }

    public async Task SetStatusAsync(string tenantId, string status, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tenants SET status = @status,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @tenantId
            """, new { tenantId, status }, cancellationToken: ct));
    }
}
