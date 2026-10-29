using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lola.Application.Pricing;

/// Shared wire shapes for the pricing surface (MPE-130): the outbox
/// payload, the MPEx `loan` member, and the idempotency hash — one
/// canonical serializer so handler, worker, and adapter always agree.
public static class PricingWire
{
    /// camelCase + null-omitted — matches MPEx's serde rename_all.
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// Outbox payload — the eval request as captured at enqueue time.
    /// The worker replays this verbatim; the application is never
    /// re-read, so a later draft patch can't mutate an in-flight eval.
    public sealed record OutboxPayload(
        PricingScenario Scenario, PricingRequestOptions Options);

    /// MPEx request body member — the LoanScenario JSON object.
    /// `scenario.asOf` only rides when it is a real timestamp: "draft"
    /// is a revision selector for the request-level asOf, not a
    /// scenario attribute (MPEx's scenario.asOf is typed DateTime).
    public static JsonObject LoanJson(PricingScenario scenario)
    {
        var node = JsonSerializer.SerializeToNode(scenario, Json)!.AsObject();
        if (node["scenario"] is JsonObject s &&
            !DateTimeOffset.TryParse(s["asOf"]?.GetValue<string>(), out _))
            s.Remove("asOf");
        return node;
    }

    /// Request-level revision selector — options wins over scenario.
    public static string? AsOf(PricingScenario scenario,
        PricingRequestOptions options)
        => options.AsOf ?? scenario.Scenario.AsOf;

    public static string PayloadJson(PricingScenario scenario,
        PricingRequestOptions options)
        => JsonSerializer.Serialize(new OutboxPayload(scenario, options), Json);

    public static OutboxPayload? ParsePayload(string payloadJson)
        => JsonSerializer.Deserialize<OutboxPayload>(payloadJson, Json);

    public static PricingEvaluation? ParseResult(string? resultJson)
        => resultJson is null ? null
            : JsonSerializer.Deserialize<PricingEvaluation>(resultJson, Json);

    public static string ResultJson(PricingEvaluation result)
        => JsonSerializer.Serialize(result, Json);

    /// Idempotency key: the app plus the canonical request. Same app
    /// asking for the same eval twice is a no-op by definition.
    public static string RequestHash(string appId, string payloadJson)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{appId}|{payloadJson}"));
        return Convert.ToHexStringLower(bytes);
    }

    public static PricingEvalDto ToDto(PricingEvalRecord r) => new(
        r.EvalId, r.ApplicationId, r.Adapter, r.AsOf, r.Status,
        ParseResult(r.ResultJson), r.Error, r.Actor,
        r.CreatedUtc, r.CompletedUtc);
}
