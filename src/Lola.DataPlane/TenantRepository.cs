using Dapper;
using Microsoft.Data.Sqlite;

namespace Lola.DataPlane;

/// Provisional generic repository base (MPE-94) — M5 reshapes this for
/// real MISMO repositories (noted on MPE-69). Every helper opens a
/// connection for the CURRENT TenantContext via the factory — like the
/// factory, this type deliberately has no tenant-id parameter, so no
/// repository code path can name another tenant's file (ADR-0003).
public abstract class TenantRepository(TenantConnectionFactory factory)
{
    /// Open a keyed connection for the scoped tenant — the ONLY way a
    /// repository reaches its file.
    protected Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
        => factory.OpenAsync(ct);

    protected async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql, object? param = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var rows = await conn.QueryAsync<T>(
            new CommandDefinition(sql, param, cancellationToken: ct));
        return rows.AsList();
    }

    protected async Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql, object? param = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<T>(
            new CommandDefinition(sql, param, cancellationToken: ct));
    }

    protected async Task<int> ExecuteAsync(
        string sql, object? param = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteAsync(
            new CommandDefinition(sql, param, cancellationToken: ct));
    }
}
