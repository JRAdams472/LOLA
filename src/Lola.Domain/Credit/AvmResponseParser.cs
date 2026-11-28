using System.Text.Json;

namespace Lola.Domain.Credit;

/// AVM response parser (M26) — HouseCanary-shaped JSON: a top-level
/// array whose first entry carries `property/value.result`. Malformed
/// JSON or missing valuation never throws — typed failure outcome.
/// Confidence is normalized through `AvmModel.NormalizeConfidence`.
public static class AvmResponseParser
{
    public sealed record ParseOutcome(
        bool Ok, string? Error, AvmModel.Result? Result);

    public static ParseOutcome Parse(string json, string vendor)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return new ParseOutcome(false, ex.Message, null);
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array
                || doc.RootElement.GetArrayLength() == 0)
                return new ParseOutcome(false,
                    "response is not a non-empty array", null);
            var entry = doc.RootElement[0];

            if (!TryPath(entry, out var result,
                    "property/value", "result"))
                return new ParseOutcome(false,
                    "no property/value.result object", null);

            if (!TryPath(result, out var value, "value")
                || !value.TryGetProperty("price_mean", out var meanEl)
                || meanEl.ValueKind != JsonValueKind.Number)
                return new ParseOutcome(false,
                    "no value.price_mean number", null);
            var mean = meanEl.GetDecimal();

            decimal? confidence = null;
            if (result.TryGetProperty("confidence_score", out var confEl)
                && confEl.ValueKind == JsonValueKind.Number)
                confidence = AvmModel.NormalizeConfidence(
                    confEl.GetDecimal());

            decimal? fsd = null;
            if (value.TryGetProperty("fsd", out var fsdEl)
                && fsdEl.ValueKind == JsonValueKind.Number)
                fsd = fsdEl.GetDecimal();

            decimal? low = NumOrNull(value, "price_lwr");
            decimal? high = NumOrNull(value, "price_upr");

            var valuationDate =
                result.TryGetProperty("valuation_date", out var vdEl)
                && vdEl.ValueKind == JsonValueKind.String
                    ? vdEl.GetString() : null;

            var model = "unknown";
            if (result.TryGetProperty("model", out var modelEl)
                && modelEl.ValueKind == JsonValueKind.Object)
            {
                var name = modelEl.TryGetProperty("name", out var nEl)
                    && nEl.ValueKind == JsonValueKind.String
                        ? nEl.GetString() : null;
                var version = modelEl.TryGetProperty("version", out var vEl)
                    && vEl.ValueKind == JsonValueKind.String
                        ? vEl.GetString() : null;
                model = name is not null
                    ? (version is not null ? $"{name} {version}" : name)
                    : model;
            }

            if (valuationDate is null)
                return new ParseOutcome(false,
                    "no valuation_date string", null);

            return new ParseOutcome(true, null, new AvmModel.Result(
                model, mean, confidence, fsd, high, low,
                valuationDate, vendor));
        }
    }

    private static bool TryPath(JsonElement root, out JsonElement hit,
        params string[] segments)
    {
        hit = root;
        foreach (var seg in segments)
        {
            if (hit.ValueKind != JsonValueKind.Object
                || !hit.TryGetProperty(seg, out hit))
                return false;
        }
        return hit.ValueKind == JsonValueKind.Object;
    }

    private static decimal? NumOrNull(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number
                ? el.GetDecimal() : null;
}
