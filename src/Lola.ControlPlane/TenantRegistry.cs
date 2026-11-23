using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Classification;
using Lola.Domain.Identity;

namespace Lola.ControlPlane;

/// Tenant registry over the control-plane SQLite store — routing and
/// provisioning metadata only (ADR-0003). `db_key_wrapped` is transit
/// ciphertext; the plaintext file key is never written here. `slug`
/// and `display_name` are ciphertext under the `control-plane` key
/// domain — the spine must stay operator-readable after tenant keys
/// are shredded (MPE-286); `slug_idx` is the keyed blind index that
/// resolves the login tenant hint.
public sealed class TenantRegistry(
    ControlPlaneDb db, IFieldCipher cipher, IBlindIndex index) : ITenantRegistry
{
    private const string Columns =
        "id, slug, display_name AS DisplayName, status, db_path AS DbPath, " +
        "db_key_wrapped AS DbKeyWrapped, created_utc AS CreatedUtc, " +
        "isppr_client_id AS IspprClientId, " +
        "isppr_customer_id AS IspprCustomerId, " +
        "isppr_key_id AS IspprKeyId, " +
        "isppr_key_wrapped AS IspprKeyWrapped";

    public async Task<TenantRecord?> FindByIdAsync(string id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<TenantRow>(
            new CommandDefinition($"SELECT {Columns} FROM tenants WHERE id = @id",
                new { id }, cancellationToken: ct));
        return row is null ? null : await ToRecordAsync(row, ct);
    }

    /// Slug-hint resolution for tenant-aware login: the keyed
    /// `slug_idx` matches the normalized slug; the slug itself never
    /// reaches the query as plaintext.
    public async Task<TenantRecord?> FindBySlugAsync(string slug, CancellationToken ct = default)
    {
        var idx = await CpCrypto.GlobalIndexAsync(index, PiiClass.Other, slug, ct);
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<TenantRow>(
            new CommandDefinition($"SELECT {Columns} FROM tenants WHERE slug_idx = @idx",
                new { idx }, cancellationToken: ct));
        return row is null ? null : await ToRecordAsync(row, ct);
    }

    public async Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<TenantRow>(
            new CommandDefinition($"SELECT {Columns} FROM tenants ORDER BY created_utc",
                cancellationToken: ct));
        var records = new List<TenantRecord>();
        foreach (var row in rows)
            records.Add(await ToRecordAsync(row, ct));
        return records;
    }

    /// INSERT OR IGNORE keeps the step re-entrant — a provisioning retry
    /// sees the existing row rather than a UNIQUE failure.
    public async Task InsertProvisioningAsync(
        string id, string slug, string displayName, string dbPath, CancellationToken ct = default)
    {
        var slugCt = await CpCrypto.EncryptGlobalAsync(cipher, slug, ct);
        var nameCt = await CpCrypto.EncryptGlobalAsync(cipher, displayName, ct);
        var slugIdx = await CpCrypto.GlobalIndexAsync(index, PiiClass.Other, slug, ct);
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT OR IGNORE INTO tenants (id, slug, slug_idx, display_name, status, db_path)
            VALUES (@id, @slug, @slugIdx, @displayName, 'provisioning', @dbPath)
            """, new { id, slug = slugCt, slugIdx, displayName = nameCt, dbPath },
            cancellationToken: ct));
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

    /// ISPPr vendor binding (MPE-190): the client/customer ids land as
    /// soon as provisioning ensures them — re-entrant, so each id is
    /// written once and never overwritten.
    public async Task SetIspprIdsAsync(string tenantId, string clientId,
        string customerId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tenants SET isppr_client_id = @clientId,
                isppr_customer_id = @customerId,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @tenantId
            """, new { tenantId, clientId, customerId },
            cancellationToken: ct));
    }

    /// The transit-wrapped tenant API key — plaintext crosses exactly
    /// once at creation, then only this ciphertext + the key id (for
    /// erasure revocation, MPE-191) persist.
    public async Task SetIspprKeyAsync(string tenantId, string keyId,
        string keyWrapped, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE tenants SET isppr_key_id = @keyId,
                isppr_key_wrapped = @keyWrapped,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE id = @tenantId
            """, new { tenantId, keyId, keyWrapped },
            cancellationToken: ct));
    }

    private async Task<TenantRecord> ToRecordAsync(TenantRow row, CancellationToken ct)
    {
        var slug = await CpCrypto.DecryptGlobalAsync(cipher, row.Slug, ct);
        var name = await CpCrypto.DecryptGlobalAsync(cipher, row.DisplayName, ct);
        return new TenantRecord(row.Id, slug!, name!, row.Status, row.DbPath,
            row.DbKeyWrapped, row.CreatedUtc, row.IspprClientId,
            row.IspprCustomerId, row.IspprKeyId, row.IspprKeyWrapped);
    }

    private sealed record TenantRow(
        string Id, string Slug, string DisplayName, string Status,
        string DbPath, string? DbKeyWrapped, string CreatedUtc,
        string? IspprClientId, string? IspprCustomerId,
        string? IspprKeyId, string? IspprKeyWrapped);
}
