using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lola.Infrastructure.Pricing;

namespace Lola.Infrastructure.Tests;

/// Live MPEx+Dex fixture — env-gated, hard-fail (never silent-skip:
/// the MPE-130 round-trip acceptance IS this suite).
///
///   MPEX_BASE_URL            http://localhost:8080
///   MPEX_TOKEN_URL           http://localhost:5556/dex/token
///   MPEX_CLIENT_ID           service client (e.g. mpex-svc)
///   MPEX_CLIENT_SECRET       its Dex secret
///   MPEX_AUDIENCE            optional
///   MPEX_SEED_CLIENT_ID      pricer-capable client for sheet seeding
///                            (e.g. mpex-ci — admin role grant)
///   MPEX_SEED_CLIENT_SECRET  its Dex secret
///
/// The fixture PUTs a deterministic draft rate sheet for CONV30
/// through MPEx's own API so the pricing leg has real priced rates to
/// return — draft fallback resolves it because nothing is published.
public sealed class MpexLiveFixture : IAsyncLifetime
{
    public MpexPricingOptions Options { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        string Require(string key) =>
            Environment.GetEnvironmentVariable(key)
            ?? throw new InvalidOperationException(
                $"{key} is required — these tests prove the MPEx " +
                "round-trip against live Dex+MPEx and never skip.");

        var baseUrl = Require("MPEX_BASE_URL");
        var tokenUrl = Require("MPEX_TOKEN_URL");
        var seedClient = Require("MPEX_SEED_CLIENT_ID");
        var seedSecret = Require("MPEX_SEED_CLIENT_SECRET");

        Options = new MpexPricingOptions(
            baseUrl, tokenUrl,
            Require("MPEX_CLIENT_ID"), Require("MPEX_CLIENT_SECRET"),
            Environment.GetEnvironmentVariable("MPEX_AUDIENCE"));

        using var http = new HttpClient();
        var token = await MintAsync(http, tokenUrl, seedClient,
            seedSecret, Options.Audience);
        using var put = new HttpRequestMessage(HttpMethod.Put,
            $"{baseUrl.TrimEnd('/')}/api/rate-sheets/CONV30")
        {
            Content = new StringContent("""
                {"spec":{"rates":[6.25,6.5,6.75],
                          "lockTerms":[30,45],
                          "prices":{"6.25:30":98.75,"6.5:30":99.25,
                                    "6.75:30":99.75,"6.25:45":98.5,
                                    "6.5:45":99.0,"6.75:45":99.5}}}
                """, Encoding.UTF8, "application/json")
        };
        put.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(put);
        res.EnsureSuccessStatusCode();
    }

    private static async Task<string> MintAsync(HttpClient http,
        string tokenUrl, string clientId, string secret,
        string? audience)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret
        };
        if (!string.IsNullOrWhiteSpace(audience))
            form["audience"] = audience;
        using var res = await http.PostAsync(tokenUrl,
            new FormUrlEncodedContent(form));
        res.EnsureSuccessStatusCode();
        var json = await res.Content
            .ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("access_token").GetString()!;
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

[CollectionDefinition("mpex")]
public sealed class MpexLiveCollection : ICollectionFixture<MpexLiveFixture>;
