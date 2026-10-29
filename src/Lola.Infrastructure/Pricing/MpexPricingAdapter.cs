using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lola.Application.Pricing;

namespace Lola.Infrastructure.Pricing;

/// MPEx adapter (MPE-130, ADR-0007): eligibility then pricing over
/// the PII-free scenario, normalized into the canonical
/// <see cref="PricingEvaluation"/>. Both calls ride a Dex
/// client-credentials token cached by <see cref="DexTokenProvider"/>;
/// a 401 invalidates the cache and retries once before surfacing as
/// a typed <see cref="PricingAdapterException"/>.
///
/// MPEx response shapes are serde_json::Value-flavored — the inbound
/// parse is deliberately defensive (node access, not a rigid DTO):
///   eligibility → { revision, eligible:[outcome], ineligible:[outcome] }
///   pricing     → { revision, priced:[{product,rateSheet,lines}],
///                   unpriced:[{product,reason}] }
///   outcome     → { product:{code,name}, eligible, reasons:[…] }
///   line        → { rate, lockTermDays, basePrice,
///                   adjustmentTotal, price }
public sealed class MpexPricingAdapter(HttpClient http,
    MpexPricingOptions options) : IPricingService
{
    public string Adapter => "mpex";

    private readonly DexTokenProvider _tokens = new(http, options);

    public async Task<PricingEvaluation> EvaluateAsync(
        PricingScenario scenario, PricingRequestOptions request,
        CancellationToken ct = default)
    {
        var loan = PricingWire.LoanJson(scenario);
        var asOf = PricingWire.AsOf(scenario, request);

        var eligibility = await PostAsync("/api/evaluate/eligibility",
            new JsonObject { ["loan"] = loan.DeepClone(), ["asOf"] = asOf }, ct);
        var outcomes = EligibilityOutcomes(eligibility);
        var eligible = outcomes.Where(o => o.Eligible).Select(o => o.Code)
            .ToHashSet(StringComparer.Ordinal);

        var offers = new Dictionary<string, List<RateOffer>>(
            StringComparer.Ordinal);
        var unpricedReasons = new Dictionary<string, string>(
            StringComparer.Ordinal);
        string? revision = eligibility?["revision"]?.GetValue<string>();

        if (!request.EligibilityOnly && eligible.Count > 0)
        {
            var wanted = request.Products is { Count: > 0 } list
                ? list.Where(eligible.Contains).ToArray()
                : eligible.ToArray();
            if (wanted.Length > 0)
            {
                var pricing = await PostAsync("/api/evaluate/pricing",
                    new JsonObject
                    {
                        ["loan"] = loan.DeepClone(),
                        ["products"] = new JsonArray(
                            wanted.Select<string, JsonNode>(c => c).ToArray()),
                        ["rate"] = request.Rate,
                        ["lockTermDays"] = request.LockTermDays,
                        ["asOf"] = asOf
                    }, ct);
                revision ??= pricing?["revision"]?.GetValue<string>();
                foreach (var p in pricing?["priced"]?.AsArray() ?? [])
                {
                    var (code, _) = ProductRef(p);
                    var sheet = p?["rateSheet"]?.GetValue<string>();
                    var lines = new List<RateOffer>();
                    foreach (var l in p?["lines"]?.AsArray() ?? [])
                    {
                        var rate = Num(l, "rate");
                        if (rate is null) continue;
                        lines.Add(new RateOffer(rate.Value, Num(l, "price"),
                            Num(l, "adjustmentTotal"), Int(l, "lockTermDays"),
                            sheet));
                    }
                    if (code is not null) offers[code] = lines;
                }
                foreach (var u in pricing?["unpriced"]?.AsArray() ?? [])
                {
                    var (code, _) = ProductRef(u);
                    var reason = u?["reason"]?.GetValue<string>();
                    if (code is not null && reason is not null)
                        unpricedReasons[code] = reason;
                }
            }
        }

        var products = outcomes.Select(o => new PricedProduct(
            o.Code, o.Name, o.Eligible,
            o.Reasons
                .Concat(unpricedReasons.TryGetValue(o.Code, out var ur)
                    ? [ur] : [])
                .ToList(),
            offers.TryGetValue(o.Code, out var l) ? l : [])).ToList();

        return new PricingEvaluation(
            $"eval-{Guid.NewGuid():N}", Adapter, asOf,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            products);
    }

    private sealed record Outcome(
        string Code, string? Name, bool Eligible, List<string> Reasons);

    private static List<Outcome> EligibilityOutcomes(JsonNode? report)
    {
        var list = new List<Outcome>();
        foreach (var bucket in new[] { "eligible", "ineligible" })
        {
            foreach (var o in report?[bucket]?.AsArray() ?? [])
            {
                var (code, name) = ProductRef(o);
                if (code is null) continue;
                var reasons = new List<string>();
                foreach (var r in o?["reasons"]?.AsArray() ?? [])
                {
                    if (r is null) continue;
                    reasons.Add(r is JsonValue v && v.TryGetValue<string>(out var s)
                        ? s : r.ToJsonString());
                }
                list.Add(new Outcome(code, name,
                    o?["eligible"]?.GetValue<bool>() ?? bucket == "eligible",
                    reasons));
            }
        }
        return list;
    }

    private static (string? code, string? name) ProductRef(JsonNode? node)
    {
        var p = node?["product"];
        return (p?["code"]?.GetValue<string>(),
            p?["name"]?.GetValue<string>());
    }

    private static decimal? Num(JsonNode? node, string key)
        => node?[key] is JsonValue v &&
           v.TryGetValue<decimal>(out var d) ? d : null;

    private static int? Int(JsonNode? node, string key)
        => node?[key] is JsonValue v &&
           v.TryGetValue<int>(out var i) ? i : null;

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
            throw new PricingAdapterException(PricingErrorKind.Unavailable,
                $"MPEx unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new PricingAdapterException(PricingErrorKind.Unavailable,
                "MPEx timed out", ex);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && !retried)
        {
            // A cached token outlived its welcome — mint fresh, retry once.
            _tokens.Invalidate();
            return await PostAsync(path, body, ct, retried: true);
        }
        if (!response.IsSuccessStatusCode)
            throw new PricingAdapterException(Classify(response.StatusCode),
                $"MPEx {path} → {(int)response.StatusCode}");
        try
        {
            return await response.Content
                .ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new PricingAdapterException(PricingErrorKind.Contract,
                $"MPEx {path} returned unparseable JSON", ex);
        }
    }

    private static PricingErrorKind Classify(
        System.Net.HttpStatusCode status) => status switch
        {
            System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden => PricingErrorKind.Auth,
            >= System.Net.HttpStatusCode.InternalServerError
                or System.Net.HttpStatusCode.TooManyRequests
                => PricingErrorKind.Unavailable,
            _ => PricingErrorKind.Rejected
        };
}
