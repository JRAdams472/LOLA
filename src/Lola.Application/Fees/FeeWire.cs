using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lola.Application.Fees;

/// Shared wire shapes for the fee surface (MPE-183): the outbox
/// payload and the idempotency hash — one canonical serializer so
/// handler, worker, and adapter always agree.
public static class FeeWire
{
    /// camelCase + null-omitted — matches Zara's serde rename_all.
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// Outbox payload — the eval request as captured at enqueue time.
    /// The worker replays this verbatim; the application is never
    /// re-read, so a later draft patch can't mutate an in-flight eval.
    public sealed record OutboxPayload(
        FeeScenario Scenario, FeeRequestOptions Options);

    public static string PayloadJson(FeeScenario scenario,
        FeeRequestOptions options)
        => JsonSerializer.Serialize(new OutboxPayload(scenario, options), Json);

    public static OutboxPayload? ParsePayload(string payloadJson)
        => JsonSerializer.Deserialize<OutboxPayload>(payloadJson, Json);

    public static FeeEvaluation? ParseResult(string? resultJson)
        => resultJson is null ? null
            : JsonSerializer.Deserialize<FeeEvaluation>(resultJson, Json);

    public static string ResultJson(FeeEvaluation result)
        => JsonSerializer.Serialize(result, Json);

    /// Idempotency key: the app plus the canonical request. Same app
    /// asking for the same eval twice is a no-op by definition.
    public static string RequestHash(string appId, string payloadJson)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{appId}|{payloadJson}"));
        return Convert.ToHexStringLower(bytes);
    }

    public static FeeEvalDto ToDto(FeeEvalRecord r) => new(
        r.EvalId, r.ApplicationId, r.Adapter, r.Status,
        ParseResult(r.ResultJson), r.Error, r.Actor,
        r.CreatedUtc, r.CompletedUtc, r.BatchId, r.Variant);
}
