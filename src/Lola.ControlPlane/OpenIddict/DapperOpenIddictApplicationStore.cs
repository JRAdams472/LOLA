using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictExceptions;

namespace Lola.ControlPlane.OpenIddict;

/// Custom Dapper IOpenIddictApplicationStore over the control-plane
/// SQLite database (ADR-0005 — no EF). LINQ-shaped store methods
/// materialize rows in memory: the applications table holds a handful
/// of registered clients, so query translation is not worth the
/// surface. Concurrency tokens rotate on every write — a stale-token
/// update fails loudly rather than last-write-wins.
public sealed class DapperOpenIddictApplicationStore(ControlPlaneDb db)
    : IOpenIddictApplicationStore<LolaApplication>
{
    private const string Columns =
        "id, client_id AS ClientId, client_secret AS ClientSecret, " +
        "client_type AS ClientType, consent_type AS ConsentType, " +
        "display_name AS DisplayName, display_names AS DisplayNames, " +
        "redirect_uris AS RedirectUris, " +
        "post_logout_redirect_uris AS PostLogoutRedirectUris, " +
        "permissions, requirements, settings, properties, " +
        "json_web_key_set AS JsonWebKeySet, " +
        "concurrency_token AS ConcurrencyToken";

    private IQueryable<LolaApplication> All()
    {
        using var conn = db.Open();
        return conn.Query<LolaApplication>($"SELECT {Columns} FROM openiddict_applications")
            .AsQueryable();
    }

    // --- queries ------------------------------------------------------

    public ValueTask<long> CountAsync(CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return new(conn.ExecuteScalar<long>("SELECT count(*) FROM openiddict_applications"));
    }

    public ValueTask<long> CountAsync<TResult>(
        Func<IQueryable<LolaApplication>, IQueryable<TResult>> query, CancellationToken cancellationToken)
        => new(query(All()).LongCount());

    public ValueTask<TResult?> GetAsync<TState, TResult>(
        Func<IQueryable<LolaApplication>, TState, IQueryable<TResult>> query,
        TState state, CancellationToken cancellationToken)
        => new(query(All(), state).FirstOrDefault());

    public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(
        Func<IQueryable<LolaApplication>, TState, IQueryable<TResult>> query,
        TState state, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in query(All(), state))
            yield return item;
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<LolaApplication> ListAsync(
        int? count, int? offset, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaApplication>(
            $"SELECT {Columns} FROM openiddict_applications ORDER BY id LIMIT @count OFFSET @offset",
            new { count = count ?? int.MaxValue, offset = offset ?? 0 });
        foreach (var row in rows) yield return row;
    }

    public async ValueTask<LolaApplication?> FindByIdAsync(string identifier, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.QuerySingleOrDefaultAsync<LolaApplication>(
            $"SELECT {Columns} FROM openiddict_applications WHERE id = @identifier",
            new { identifier });
    }

    public async ValueTask<LolaApplication?> FindByClientIdAsync(string identifier, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        return await conn.QuerySingleOrDefaultAsync<LolaApplication>(
            $"SELECT {Columns} FROM openiddict_applications WHERE client_id = @identifier",
            new { identifier });
    }

    public async IAsyncEnumerable<LolaApplication> FindByRedirectUriAsync(
        string uri, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaApplication>($"""
            SELECT {Columns} FROM openiddict_applications
            WHERE EXISTS (SELECT 1 FROM json_each(redirect_uris) WHERE value = @uri)
            """, new { uri });
        foreach (var row in rows) yield return row;
    }

    public async IAsyncEnumerable<LolaApplication> FindByPostLogoutRedirectUriAsync(
        string uri, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<LolaApplication>($"""
            SELECT {Columns} FROM openiddict_applications
            WHERE EXISTS (SELECT 1 FROM json_each(post_logout_redirect_uris) WHERE value = @uri)
            """, new { uri });
        foreach (var row in rows) yield return row;
    }

    // --- getters --------------------------------------------------------

    public ValueTask<LolaApplication> InstantiateAsync(CancellationToken cancellationToken)
        => new(new LolaApplication());

    public ValueTask<string?> GetIdAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(application.Id);

    public ValueTask<string?> GetClientIdAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(application.ClientId);

    public ValueTask<string?> GetClientSecretAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(application.ClientSecret);

    public ValueTask<string?> GetClientTypeAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(application.ClientType);

    public ValueTask<string?> GetApplicationTypeAsync(LolaApplication application, CancellationToken cancellationToken)
        => new((string?)null); // application types unused — we only register confidential/public clients

    public ValueTask<string?> GetConsentTypeAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(application.ConsentType);

    public ValueTask<string?> GetDisplayNameAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(application.DisplayName);

    public ValueTask<ImmutableDictionary<CultureInfo, string>> GetDisplayNamesAsync(
        LolaApplication application, CancellationToken cancellationToken)
        => new(OidcJson.JsonToCultureDict(application.DisplayNames));

    public ValueTask<ImmutableArray<string>> GetPermissionsAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(OidcJson.JsonToArray(application.Permissions));

    public ValueTask<ImmutableArray<string>> GetPostLogoutRedirectUrisAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(OidcJson.JsonToArray(application.PostLogoutRedirectUris));

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(OidcJson.JsonToProperties(application.Properties));

    public ValueTask<ImmutableArray<string>> GetRedirectUrisAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(OidcJson.JsonToArray(application.RedirectUris));

    public ValueTask<ImmutableArray<string>> GetRequirementsAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(OidcJson.JsonToArray(application.Requirements));

    public ValueTask<ImmutableDictionary<string, string>> GetSettingsAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(OidcJson.JsonToStringDict(application.Settings));

    public ValueTask<JsonWebKeySet?> GetJsonWebKeySetAsync(LolaApplication application, CancellationToken cancellationToken)
        => new(OidcJson.JsonToJwks(application.JsonWebKeySet));

    // --- setters -------------------------------------------------------

    public ValueTask SetClientIdAsync(LolaApplication application, string? identifier, CancellationToken cancellationToken)
    {
        application.ClientId = identifier;
        return default;
    }

    public ValueTask SetClientSecretAsync(LolaApplication application, string? secret, CancellationToken cancellationToken)
    {
        application.ClientSecret = secret;
        return default;
    }

    public ValueTask SetClientTypeAsync(LolaApplication application, string? type, CancellationToken cancellationToken)
    {
        application.ClientType = type;
        return default;
    }

    public ValueTask SetApplicationTypeAsync(LolaApplication application, string? type, CancellationToken cancellationToken)
        => default;

    public ValueTask SetConsentTypeAsync(LolaApplication application, string? type, CancellationToken cancellationToken)
    {
        application.ConsentType = type;
        return default;
    }

    public ValueTask SetDisplayNameAsync(LolaApplication application, string? name, CancellationToken cancellationToken)
    {
        application.DisplayName = name;
        return default;
    }

    public ValueTask SetDisplayNamesAsync(LolaApplication application,
        ImmutableDictionary<CultureInfo, string> names, CancellationToken cancellationToken)
    {
        application.DisplayNames = OidcJson.DictToJson(names);
        return default;
    }

    public ValueTask SetPermissionsAsync(LolaApplication application,
        ImmutableArray<string> permissions, CancellationToken cancellationToken)
    {
        application.Permissions = OidcJson.ArrayToJson(permissions);
        return default;
    }

    public ValueTask SetPostLogoutRedirectUrisAsync(LolaApplication application,
        ImmutableArray<string> uris, CancellationToken cancellationToken)
    {
        application.PostLogoutRedirectUris = OidcJson.ArrayToJson(uris);
        return default;
    }

    public ValueTask SetPropertiesAsync(LolaApplication application,
        ImmutableDictionary<string, JsonElement> properties, CancellationToken cancellationToken)
    {
        application.Properties = OidcJson.PropertiesToJson(properties);
        return default;
    }

    public ValueTask SetRedirectUrisAsync(LolaApplication application,
        ImmutableArray<string> uris, CancellationToken cancellationToken)
    {
        application.RedirectUris = OidcJson.ArrayToJson(uris);
        return default;
    }

    public ValueTask SetRequirementsAsync(LolaApplication application,
        ImmutableArray<string> requirements, CancellationToken cancellationToken)
    {
        application.Requirements = OidcJson.ArrayToJson(requirements);
        return default;
    }

    public ValueTask SetSettingsAsync(LolaApplication application,
        ImmutableDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        application.Settings = settings.Count == 0
            ? null
            : JsonSerializer.Serialize(settings);
        return default;
    }

    public ValueTask SetJsonWebKeySetAsync(LolaApplication application, JsonWebKeySet? set, CancellationToken cancellationToken)
    {
        application.JsonWebKeySet = set is null ? null : JsonSerializer.Serialize(set);
        return default;
    }

    // --- writes --------------------------------------------------------

    public async ValueTask CreateAsync(LolaApplication application, CancellationToken cancellationToken)
    {
        application.Id = string.IsNullOrEmpty(application.Id)
            ? Guid.NewGuid().ToString("N")
            : application.Id;
        application.ConcurrencyToken = OidcJson.NewConcurrencyToken();
        using var conn = db.Open();
        await conn.ExecuteAsync("""
            INSERT INTO openiddict_applications
                (id, client_id, client_secret, client_type, consent_type,
                 display_name, display_names, redirect_uris,
                 post_logout_redirect_uris, permissions, requirements,
                 settings, properties, json_web_key_set, concurrency_token)
            VALUES (@Id, @ClientId, @ClientSecret, @ClientType, @ConsentType,
                 @DisplayName, @DisplayNames, @RedirectUris,
                 @PostLogoutRedirectUris, @Permissions, @Requirements,
                 @Settings, @Properties, @JsonWebKeySet, @ConcurrencyToken)
            """, application);
    }

    public async ValueTask UpdateAsync(LolaApplication application, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var token = OidcJson.NewConcurrencyToken();
        var affected = await conn.ExecuteAsync("""
            UPDATE openiddict_applications SET
                client_id = @ClientId, client_secret = @ClientSecret,
                client_type = @ClientType, consent_type = @ConsentType,
                display_name = @DisplayName, display_names = @DisplayNames,
                redirect_uris = @RedirectUris,
                post_logout_redirect_uris = @PostLogoutRedirectUris,
                permissions = @Permissions, requirements = @Requirements,
                settings = @Settings, properties = @Properties,
                json_web_key_set = @JsonWebKeySet,
                concurrency_token = @token
            WHERE id = @Id AND concurrency_token = @ConcurrencyToken
            """, new
        {
            application.ClientId,
            application.ClientSecret,
            application.ClientType,
            application.ConsentType,
            application.DisplayName,
            application.DisplayNames,
            application.RedirectUris,
            application.PostLogoutRedirectUris,
            application.Permissions,
            application.Requirements,
            application.Settings,
            application.Properties,
            application.JsonWebKeySet,
            application.Id,
            application.ConcurrencyToken,
            token
        });
        if (affected == 0)
            throw new ConcurrencyException("The application was modified concurrently.");
        application.ConcurrencyToken = token;
    }

    public async ValueTask DeleteAsync(LolaApplication application, CancellationToken cancellationToken)
    {
        using var conn = db.Open();
        var affected = await conn.ExecuteAsync("""
            DELETE FROM openiddict_applications
            WHERE id = @Id AND concurrency_token = @ConcurrencyToken
            """, application);
        if (affected == 0)
            throw new ConcurrencyException("The application was modified concurrently.");
    }
}
