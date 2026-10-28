using Dapper;
using Lola.Application.Identity;

namespace Lola.ControlPlane;

/// Per-tenant migration rollout records (MPE-93) — operator-facing
/// status; the in-file DbUp journal remains authoritative.
public sealed class TenantMigrationTracker(ControlPlaneDb db) : IMigrationTracker
{
    public async Task RecordAsync(string tenantId, int version, string name,
        string status, string? error, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tenant_migrations (tenant_id, version, name, applied_utc, status, error)
            VALUES (@tenantId, @version, @name, @appliedUtc, @status, @error)
            ON CONFLICT (tenant_id, version, name) DO UPDATE SET
                applied_utc = @appliedUtc, status = @status, error = @error
            """, new
        {
            tenantId,
            version,
            name,
            appliedUtc = DateTimeOffset.UtcNow.ToString("O"),
            status,
            error
        }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<MigrationRow>> ListAsync(
        string? tenantId = null, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<MigrationRow>(new CommandDefinition("""
            SELECT tenant_id AS TenantId, version AS Version, name AS Name,
                   applied_utc AS AppliedUtc, status AS Status, error AS Error
            FROM tenant_migrations
            WHERE (@tenantId IS NULL OR tenant_id = @tenantId)
            ORDER BY tenant_id, version
            """, new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }
}
