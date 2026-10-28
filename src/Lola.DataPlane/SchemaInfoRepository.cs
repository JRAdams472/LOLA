namespace Lola.DataPlane;

/// What the current tenant file reports about its own schema state —
/// the baseline `schema_info` row plus the DbUp journal
/// (`SchemaVersions`, authoritative for applied scripts).
public sealed record TenantSchemaInfo(
    int Version, IReadOnlyList<string> AppliedMigrations);

/// First consumer of the provisional <see cref="TenantRepository"/>
/// seam (MPE-94) — proves the shape end-to-end and gives ops a way to
/// answer "what migration level is this tenant file at?" without
/// touching the file by hand.
public sealed class SchemaInfoRepository(TenantConnectionFactory factory)
    : TenantRepository(factory)
{
    public async Task<TenantSchemaInfo> GetAsync(CancellationToken ct = default)
    {
        var version = await QuerySingleOrDefaultAsync<long>(
            "SELECT version FROM schema_info LIMIT 1", ct: ct);
        var scripts = await QueryAsync<string>(
            "SELECT ScriptName FROM SchemaVersions ORDER BY SchemaVersionID",
            ct: ct);
        return new TenantSchemaInfo((int)version, scripts);
    }
}
