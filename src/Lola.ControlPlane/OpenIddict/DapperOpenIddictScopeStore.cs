using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictExceptions;

namespace Lola.ControlPlane.OpenIddict;

/// Custom Dapper IOpenIddictScopeStore (ADR-0005).
public sealed class DapperOpenIddictScopeStore(ControlPlaneDb db)
    : IOpenIddictScopeStore<LolaScope>
{
    private const string Columns =
        "id, name, description, descriptions, display_name AS DisplayName, " +
        "display_names AS DisplayNames, properties, resources, " +
        "concurrency_token AS ConcurrencyToken";

    private IQueryable<LolaScope> All()
    {
        using var conn = db.Open();
        return conn.Query<LolaScope>($"SELECT {Columns} FROM openiddict_scopes")
            .AsQueryable();
    }

    public ValueTask<long> CountAsync(CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return new(conn.ExecuteScalar<long>("SELECT count(*) FROM openiddict_scopes"));
    }

    public ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<LolaScope>, IQueryable<TResult>> query, CancellationToken cancellationToken)
        => new(query(All()).LongCount());

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<LolaScope>, TState, IQueryable<TResult>> query,
        TState state, CancellationToken cancellationToken)
        => new(query(All(), state).FirstOrDefault());

    public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<LolaScope>, TState, IQueryable<TResult>> query,
        TState state, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in query(All(), state))
            yield return item;
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<LolaScope> ListAsync(
        int? count, int? offset, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaScope>(
            $"SELECT {Columns} FROM openiddict_scopes ORDER BY id LIMIT @count OFFSET @offset",
            new { count = count ?? int.MaxValue, offset = offset ?? 0 });
        foreach (var row in rows) yield return row;
    }

    public async ValueTask<LolaScope?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.QuerySingleOrDefaultAsync<LolaScope>(
            $"SELECT {Columns} FROM openiddict_scopes WHERE id = @identifier",
            new { identifier });
    }

    public async ValueTask<LolaScope?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.QuerySingleOrDefaultAsync<LolaScope>(
            $"SELECT {Columns} FROM openiddict_scopes WHERE name = @name",
            new { name });
    }

    public async IAsyncEnumerable<LolaScope> FindByNamesAsync(
        ImmutableArray<string> names, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (names.IsDefaultOrEmpty) yield break;
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaScope>(
            $"SELECT {Columns} FROM openiddict_scopes WHERE name IN @names",
            new { names = names.ToArray() });
        foreach (var row in rows) yield return row;
    }

    public async IAsyncEnumerable<LolaScope> FindByResourceAsync(
        string resource, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaScope>($"""
            SELECT {Columns} FROM openiddict_scopes
            WHERE EXISTS (SELECT 1 FROM json_each(resources) WHERE value = @resource)
            """, new { resource });
        foreach (var row in rows) yield return row;
    }

    // --- getters ------------------------------------------------------

    public ValueTask<LolaScope> InstantiateAsync(CancellationToken cancellationToken)
        => new(new LolaScope());

    public ValueTask<string?> GetIdAsync(LolaScope scope, CancellationToken cancellationToken)
        => new(scope.Id);

    public ValueTask<string?> GetNameAsync(LolaScope scope, CancellationToken cancellationToken)
        => new(scope.Name);

    public ValueTask<string?> GetDescriptionAsync(LolaScope scope, CancellationToken cancellationToken)
        => new(scope.Description);

    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDescriptionsAsync(LolaScope scope, CancellationToken cancellationToken)
        => new(OidcJson.JsonToCultureDict(scope.Descriptions));

    public ValueTask<string?> GetDisplayNameAsync(LolaScope scope, CancellationToken cancellationToken)
        => new(scope.DisplayName);

    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(LolaScope scope, CancellationToken cancellationToken)
        => new(OidcJson.JsonToCultureDict(scope.DisplayNames));

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(LolaScope scope, CancellationToken cancellationToken)
        => new(OidcJson.JsonToProperties(scope.Properties));

    public ValueTask<ImmutableArray<string>> GetResourcesAsync(LolaScope scope, CancellationToken cancellationToken)
        => new(OidcJson.JsonToArray(scope.Resources));

    // --- setters --------------------------------------------------------

    public ValueTask SetNameAsync(LolaScope scope, string? name, CancellationToken cancellationToken)
    {
        scope.Name = name;
        return default;
    }

    public ValueTask SetDescriptionAsync(LolaScope scope, string? description, CancellationToken cancellationToken)
    {
        scope.Description = description;
        return default;
    }

    public ValueTask SetDescriptionsAsync(LolaScope scope,
        ImmutableDictionary<CultureInfo, string> descriptions, CancellationToken cancellationToken)
    {
        scope.Descriptions = OidcJson.DictToJson(descriptions);
        return default;
    }

    public ValueTask SetDisplayNameAsync(LolaScope scope, string? name, CancellationToken cancellationToken)
    {
        scope.DisplayName = name;
        return default;
    }

    public ValueTask SetDisplayNamesAsync(LolaScope scope,
        ImmutableDictionary<CultureInfo, string> names, CancellationToken cancellationToken)
    {
        scope.DisplayNames = OidcJson.DictToJson(names);
        return default;
    }

    public ValueTask SetPropertiesAsync(LolaScope scope,
        ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken)
    {
        scope.Properties = OidcJson.PropertiesToJson(properties);
        return default;
    }

    public ValueTask SetResourcesAsync(LolaScope scope,
        ImmutableArray<string> resources, CancellationToken cancellationToken)
    {
        scope.Resources = OidcJson.ArrayToJson(resources);
        return default;
    }

    // --- writes --------------------------------------------------------

    public async ValueTask CreateAsync(LolaScope scope, CancellationToken cancellationToken)
    {
        scope.Id = string.IsNullOrEmpty(scope.Id) ? Guid.NewGuid().ToString("N") : scope.Id;
        scope.ConcurrencyToken = OidcJson.NewConcurrencyToken();
        using var conn = db.Open();
        await conn.ExecuteAsync("""
            INSERT INTO openiddict_scopes
                (id, name, description, descriptions, display_name,
                 display_names, properties, resources, concurrency_token)
            VALUES (@Id, @Name, @Description, @Descriptions, @DisplayName,
                 @DisplayNames, @Properties, @Resources, @ConcurrencyToken)
            """, scope);
    }

    public async ValueTask UpdateAsync(LolaScope scope, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var token = OidcJson.NewConcurrencyToken();
        var affected = await conn.ExecuteAsync("""
            UPDATE openiddict_scopes SET
                name = @Name, description = @Description,
                descriptions = @Descriptions, display_name = @DisplayName,
                display_names = @DisplayNames, properties = @Properties,
                resources = @Resources, concurrency_token = @token
            WHERE id = @Id AND concurrency_token = @ConcurrencyToken
            """, new
        {
            scope.Name,
            scope.Description,
            scope.Descriptions,
            scope.DisplayName,
            scope.DisplayNames,
            scope.Properties,
            scope.Resources,
            scope.Id,
            scope.ConcurrencyToken,
            token
        });
        if (affected == 0)
            throw new ConcurrencyException("The scope was modified concurrently.");
        scope.ConcurrencyToken = token;
    }

    public async ValueTask DeleteAsync(LolaScope scope, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var affected = await conn.ExecuteAsync("""
            DELETE FROM openiddict_scopes
            WHERE id = @Id AND concurrency_token = @ConcurrencyToken
            """, scope);
        if (affected == 0)
            throw new ConcurrencyException("The scope was modified concurrently.");
    }
}
