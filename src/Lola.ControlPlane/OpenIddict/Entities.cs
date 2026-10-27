using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Lola.ControlPlane.OpenIddict;

/// POCOs backing the custom Dapper OpenIddict stores (ADR-0005 — no
/// EF). JSON-shaped columns stay raw strings here; encode/decode
/// happens at the store boundary so SQL stays simple.
public sealed class LolaApplication
{
    public string Id { get; set; } = "";
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? ClientType { get; set; }
    public string? ConsentType { get; set; }
    public string? DisplayName { get; set; }
    public string? DisplayNames { get; set; }
    public string? RedirectUris { get; set; }
    public string? PostLogoutRedirectUris { get; set; }
    public string? Permissions { get; set; }
    public string? Requirements { get; set; }
    public string? Settings { get; set; }
    public string? Properties { get; set; }
    public string? JsonWebKeySet { get; set; }
    public string ConcurrencyToken { get; set; } = "";
}

public sealed class LolaAuthorization
{
    public string Id { get; set; } = "";
    public string? ApplicationId { get; set; }
    public string? CreationDate { get; set; }
    public string? Properties { get; set; }
    public string? Scopes { get; set; }
    public string? Status { get; set; }
    public string? Subject { get; set; }
    public string? Type { get; set; }
    public string ConcurrencyToken { get; set; } = "";
}

public sealed class LolaScope
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Descriptions { get; set; }
    public string? DisplayName { get; set; }
    public string? DisplayNames { get; set; }
    public string? Properties { get; set; }
    public string? Resources { get; set; }
    public string ConcurrencyToken { get; set; } = "";
}

public sealed class LolaToken
{
    public string Id { get; set; } = "";
    public string? ApplicationId { get; set; }
    public string? AuthorizationId { get; set; }
    public string? CreationDate { get; set; }
    public string? ExpirationDate { get; set; }
    public string? Payload { get; set; }
    public string? Properties { get; set; }
    public string? RedemptionDate { get; set; }
    public string? ReferenceId { get; set; }
    public string? Status { get; set; }
    public string? Subject { get; set; }
    public string? Type { get; set; }
    public string ConcurrencyToken { get; set; } = "";
}

/// Shared JSON + date plumbing for the stores — keeps every store
/// file free of serialization noise.
internal static class OidcJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string? ArrayToJson(IReadOnlyCollection<string>? values)
        => values is { Count: > 0 } ? JsonSerializer.Serialize(values, Options) : null;

    public static ImmutableArray<string> JsonToArray(string? json)
        => string.IsNullOrEmpty(json)
            ? ImmutableArray<string>.Empty
            : JsonSerializer.Deserialize<ImmutableArray<string>>(json, Options);

    public static string? DictToJson(IReadOnlyDictionary<CultureInfo, string>? values)
        => values is { Count: > 0 }
            ? JsonSerializer.Serialize(
                values.ToDictionary(kv => kv.Key.Name, kv => kv.Value), Options)
            : null;

    public static ImmutableDictionary<CultureInfo, string> JsonToCultureDict(string? json)
        => string.IsNullOrEmpty(json)
            ? ImmutableDictionary<CultureInfo, string>.Empty
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options)!
                .ToImmutableDictionary(kv => CultureInfo.GetCultureInfo(kv.Key), kv => kv.Value);

    public static ImmutableDictionary<string, string> JsonToStringDict(string? json)
        => string.IsNullOrEmpty(json)
            ? ImmutableDictionary<string, string>.Empty
            : JsonSerializer.Deserialize<ImmutableDictionary<string, string>>(json, Options);

    public static string? PropertiesToJson(IReadOnlyDictionary<string, JsonElement>? values)
        => values is { Count: > 0 } ? JsonSerializer.Serialize(values, Options) : null;

    public static ImmutableDictionary<string, JsonElement> JsonToProperties(string? json)
        => string.IsNullOrEmpty(json)
            ? ImmutableDictionary<string, JsonElement>.Empty
            : JsonSerializer.Deserialize<ImmutableDictionary<string, JsonElement>>(json, Options);

    public static JsonWebKeySet? JsonToJwks(string? json)
        => string.IsNullOrEmpty(json) ? null : new JsonWebKeySet(json);

    public static string? DateToText(DateTimeOffset? value)
        => value?.ToString("O");

    public static DateTimeOffset? TextToDate(string? text)
        => string.IsNullOrEmpty(text) ? null : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);

    public static string NewConcurrencyToken() => Guid.NewGuid().ToString("N");
}
