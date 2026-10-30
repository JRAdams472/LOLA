using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lola.Application.Pricing;
using Lola.Infrastructure.Pricing;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// MPE-130 acceptance, live: Dex client-credentials token → MPEx
/// eligibility → pricing for eligible products → normalized
/// PricingEvaluation with real priced rates. Env-gated like OpenBao —
/// the suite hard-fails without MPEX_* rather than silently passing.
[Collection("mpex")]
public sealed class MpexLiveTests(MpexLiveFixture fixture)
{
    private static readonly PricingScenario Conv30 = new(
        new LoanSection("purchase", "conventional", 350_000m, 360,
            "fixed", null, 75m, 75m, 1),
        new PropertySection("sfr", "primary", "IL", 400_000m, null),
        new BorrowerSection(720, "FICO", false, false),
        new ScenarioSection(30, null));

    [Fact]
    public async Task Round_trip_returns_eligibility_and_priced_rates()
    {
        using var http = new HttpClient();
        var adapter = new MpexPricingAdapter(http, fixture.Options);

        var eval = await adapter.EvaluateAsync(
            Conv30, new PricingRequestOptions());

        eval.Adapter.ShouldBe("mpex");
        eval.Products.ShouldNotBeEmpty();
        var conv = eval.Products
            .Single(p => p.ProductCode == "CONV30");
        conv.Eligible.ShouldBeTrue();
        conv.Offers.ShouldNotBeEmpty();

        // Normalization pass-through, verified against the raw MPEx
        // response rather than pinned numbers — the dev stack holds
        // a published CONV30 sheet whose prices beat our draft.
        var raw = await RawPricingAsync(http);
        var line = raw["priced"]![0]!["lines"]!.AsArray()
            .Select(l => (Rate: l!["rate"]!.GetValue<decimal>(),
                Price: l!["price"]!.GetValue<decimal>()))
            .OrderBy(x => x.Rate).ToArray();
        var offer = conv.Offers.OrderBy(o => o.Rate).ToArray();
        offer.Length.ShouldBe(line.Length);
        for (var i = 0; i < offer.Length; i++)
        {
            offer[i].Rate.ShouldBe(line[i].Rate);
            offer[i].Price.ShouldBe(line[i].Price);
        }
        offer[0].LockTermDays.ShouldNotBeNull();
        offer[0].Margin.ShouldNotBeNull();     // adjustmentTotal maps
        offer[0].InvestorName.ShouldBe("CONV30"); // rateSheet key

        // An ineligible product reports WHY, structurally.
        eval.Products.ShouldContain(p => !p.Eligible &&
            p.IneligibilityReasons.Count > 0);
    }

    /// asOf resolves the historic snapshot (MPE-132): publish v1,
    /// capture the boundary, publish a distinct v2 — an asOf between
    /// them must return v1's prices while `latest` returns v2's.
    /// The assertion compares the two evals' price DELTA to the sheet
    /// delta (adjustments are revision-independent for equal specs).
    [Fact]
    public async Task AsOf_returns_historic_snapshot_distinct_from_latest()
    {
        // v1 = fixture's draft (6.5:30 = 99.25). Publishing may 409 if
        // an identical published row already exists — either way the
        // snapshot state at the boundary holds v1's prices.
        try { await fixture.PublishAsync("lola-asof-v1"); }
        catch (HttpRequestException e) when
            ((int?)e.StatusCode == 409)
        { }
        var asOf = DateTimeOffset.UtcNow
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await Task.Delay(150); // keep the boundary strictly < v2's stamp

        // v2 prices differ from v1 by exactly -1.65 on 6.5:30.
        await fixture.PutSheetAsync("""
            {"spec":{"rates":[6.25,6.5,6.75],
                      "lockTerms":[30,45],
                      "prices":{"6.25:30":97.10,"6.5:30":97.60,
                                "6.75:30":98.10,"6.25:45":96.85,
                                "6.5:45":97.35,"6.75:45":97.85}}}
            """);
        await fixture.PublishAsync("lola-asof-v2");

        using var http = new HttpClient();
        var adapter = new MpexPricingAdapter(http, fixture.Options);
        var latest = await adapter.EvaluateAsync(
            Conv30, new PricingRequestOptions());
        var historic = await adapter.EvaluateAsync(
            Conv30, new PricingRequestOptions(AsOf: asOf));

        decimal Price(PricingEvaluation e) => e.Products
            .Single(p => p.ProductCode == "CONV30").Offers
            .Single(o => o.Rate == 6.5m && o.LockTermDays == 30)
            .Price!.Value;
        (Price(historic) - Price(latest)).ShouldBe(1.65m);
    }

    /// Same pricing call the adapter makes — for pass-through
    /// comparison of the normalized offers.
    private async Task<JsonNode> RawPricingAsync(HttpClient http)
    {
        var token = await new DexTokenProviderPublic(fixture.Options)
            .MintAsync(http);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{fixture.Options.BaseUrl.TrimEnd('/')}/api/evaluate/pricing")
        {
            Content = new StringContent("""
                {"loan":{"loan":{"type":"conventional","amount":350000,
                    "termMonths":360,"amortization":"fixed","ltv":75,
                    "units":1},
                 "property":{"type":"sfr","occupancy":"primary",
                    "state":"IL"},
                 "borrower":{"creditScore":720,"creditModel":"FICO"},
                 "scenario":{"lockTermDays":30}},
                 "products":["CONV30"],"lockTermDays":30}
                """, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", token);
        var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    private sealed class DexTokenProviderPublic(MpexPricingOptions o)
    {
        public async Task<string> MintAsync(HttpClient http)
        {
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = o.ClientId,
                ["client_secret"] = o.ClientSecret
            };
            if (!string.IsNullOrWhiteSpace(o.Audience))
                form["audience"] = o.Audience;
            var res = await http.PostAsync(o.TokenUrl,
                new FormUrlEncodedContent(form));
            res.EnsureSuccessStatusCode();
            var json = await res.Content
                .ReadFromJsonAsync<JsonElement>();
            return json.GetProperty("access_token").GetString()!;
        }
    }

    [Fact]
    public async Task Eligibility_only_returns_products_no_offers()
    {
        var adapter = new MpexPricingAdapter(
            new HttpClient(), fixture.Options);

        var eval = await adapter.EvaluateAsync(Conv30,
            new PricingRequestOptions(EligibilityOnly: true));

        eval.Products.ShouldContain(
            p => p.ProductCode == "CONV30" && p.Eligible);
        eval.Products.All(p => p.Offers.Count == 0).ShouldBeTrue();
    }
}
