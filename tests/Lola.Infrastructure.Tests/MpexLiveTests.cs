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
