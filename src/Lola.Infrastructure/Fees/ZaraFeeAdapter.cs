using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Lola.Application.Fees;

namespace Lola.Infrastructure.Fees;

/// Zara adapter (MPE-183, ADR-0007): one POST to
/// `/api/evaluate/{env}` carrying the PII-free
/// <see cref="FeeScenario"/> under `scenario`, normalized into the
/// canonical <see cref="FeeEvaluation"/>. The bearer token comes
/// from <see cref="ZaraTokenProvider"/> (env or re-readable file);
/// a 401 invalidates the cache and retries once before surfacing as
/// a typed <see cref="FeeAdapterException"/>.
///
/// Zara's EvalReport shape — parsed defensively (node access, not a
/// rigid DTO):
///   { fees:[{ artifact, feeCode, tridBucket, base,
///             adjustments:[{ rule, op, value, runningTotal }],
///             total }],
///     total, elapsedMs, fingerprint }
///
/// Vendor transform: Zara's inbound PII guard denies `zip`/`postal`
/// field names outright (its own ADR-0005) — its fee rules key on
/// county/state, so the adapter omits property ZIP from the wire
/// scenario. The canonical <see cref="FeeScenario"/> still carries
/// ZIP for vendors whose contracts accept it.
public sealed class ZaraFeeAdapter(HttpClient http,
    ZaraFeeOptions options) : IFeeService
{
    public string Adapter => "zara";

    private readonly ZaraTokenProvider _tokens = new(options);

    public async Task<FeeEvaluation> EvaluateAsync(
        FeeScenario scenario, FeeRequestOptions request,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["tenantId"] = options.TenantId,
            ["scenario"] = JsonSerializerToNode(scenario)
        };
        var report = await PostAsync(
            $"/api/evaluate/{options.Env}", body, ct);

        var items = new List<FeeLine>();
        foreach (var f in report?["fees"]?.AsArray() ?? [])
        {
            var feeCode = f?["feeCode"]?.GetValue<string>();
            if (feeCode is null) continue;
            var adjustments = new List<FeeAdjustment>();
            foreach (var a in f?["adjustments"]?.AsArray() ?? [])
            {
                adjustments.Add(new FeeAdjustment(
                    a?["rule"]?.GetValue<string>() ?? "",
                    a?["op"]?.GetValue<string>() ?? "",
                    Num(a, "value") ?? 0m,
                    Num(a, "runningTotal") ?? 0m));
            }
            items.Add(new FeeLine(feeCode,
                f?["artifact"]?.GetValue<string>(),
                f?["tridBucket"]?.GetValue<string>() ?? "",
                Num(f, "base") ?? 0m, adjustments,
                Num(f, "total") ?? 0m));
        }

        // FeeCodes is an eval-time filter (same contract as the
        // stub) — the request shape Zara sees is unchanged.
        if (request.FeeCodes is { } codes)
            items = items.Where(i => codes.Contains(i.FeeCode)).ToList();

        return new FeeEvaluation(
            $"eval-{Guid.NewGuid():N}", Adapter,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            report?["fingerprint"]?.GetValue<string>(), items);
    }

    private static JsonNode? JsonSerializerToNode(FeeScenario scenario)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(
            scenario, FeeWire.Json);
        if (node?["property"] is JsonObject p) p.Remove("zip");
        return node;
    }

    private static decimal? Num(JsonNode? node, string key)
        => node?[key] is JsonValue v &&
           v.TryGetValue<decimal>(out var d) ? d : null;

    private async Task<JsonNode?> PostAsync(string path, JsonObject body,
        CancellationToken ct, bool retried = false)
    {
        var token = await _tokens.GetAsync(ct);
        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"{options.BaseUrl.TrimEnd('/')}{path}")
        {
            Content = new StringContent(body.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new FeeAdapterException(FeeErrorKind.Unavailable,
                $"Zara unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new FeeAdapterException(FeeErrorKind.Unavailable,
                "Zara timed out", ex);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && !retried)
        {
            // A cached file token outlived its welcome — re-read
            // (an external re-mint may have landed), retry once.
            _tokens.Invalidate();
            return await PostAsync(path, body, ct, retried: true);
        }
        if (!response.IsSuccessStatusCode)
            throw new FeeAdapterException(Classify(response.StatusCode),
                $"Zara {path} → {(int)response.StatusCode}");
        try
        {
            return await response.Content
                .ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new FeeAdapterException(FeeErrorKind.Contract,
                $"Zara {path} returned unparseable JSON", ex);
        }
    }

    private static FeeErrorKind Classify(System.Net.HttpStatusCode code)
        => (int)code switch
        {
            401 => FeeErrorKind.Auth,
            400 or 403 or 404 or 422 => FeeErrorKind.Rejected,
            >= 500 => FeeErrorKind.Unavailable,
            _ => FeeErrorKind.Rejected
        };
}
