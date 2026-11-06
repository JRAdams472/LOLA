using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Lola.Application.Fees;
using Lola.Infrastructure.Fees;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// MPE-183 acceptance, live: file/env JWT → Zara
/// `POST /api/evaluate/{env}` → normalized FeeEvaluation whose items
/// carry real TRID buckets from the seeded acme-mortgage snapshot.
/// Env-gated like OpenBao/MPEx — hard-fails without ZARA_*.
[Collection("zara")]
public sealed class ZaraLiveTests(ZaraLiveFixture fixture)
{
    private static readonly FeeScenario Conv30 = new(
        new FeeLoanSection("purchase", "conventional", 350_000m, 360),
        new FeePropertySection("IL", "62701", "Sangamon", 475_000m,
            560_000m),
        new FeeBorrowerSection(false, false, false));

    private static JsonNode? StrippedScenario()
    {
        var node = JsonNode.Parse(
            FeeWire.PayloadJson(Conv30, new FeeRequestOptions()))
            !["scenario"]!.DeepClone();
        if (node?["property"] is JsonObject p) p.Remove("zip");
        return node;
    }

    [Fact]
    public async Task Round_trip_returns_itemized_le_sections()
    {
        using var http = new HttpClient();
        var adapter = new ZaraFeeAdapter(http, fixture.Options);

        var eval = await adapter.EvaluateAsync(
            Conv30, new FeeRequestOptions());

        eval.Adapter.ShouldBe("zara");
        eval.Items.ShouldNotBeEmpty();
        eval.Fingerprint.ShouldNotBeNull();
        // TRID buckets land as LE sections where the artifact has one
        // — the seeded snapshot also carries bucket-less artifacts.
        eval.Items.Any(i => i.Section.Length > 0).ShouldBeTrue();
        eval.Items.All(i => i.FeeCode.Length > 0).ShouldBeTrue();

        // Pass-through equivalence: the raw engine response and the
        // normalized eval agree line-for-line — the assertion is
        // shape + identity, not pinned demo amounts (the published
        // snapshot may evolve).
        var token = fixture.Options.Token
            ?? File.ReadAllText(fixture.Options.TokenFile!).Trim();
        using var probe = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"{fixture.Options.BaseUrl.TrimEnd('/')}" +
            $"/api/evaluate/{fixture.Options.Env}")
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["tenantId"] = fixture.Options.TenantId,
                    // Same vendor transform as the adapter — Zara's
                    // inbound PII guard denies `zip` outright.
                    ["scenario"] = StrippedScenario()
                }.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", token);
        var raw = await (await probe.SendAsync(req))
            .Content.ReadFromJsonAsync<JsonNode>();

        var rawFees = raw!["fees"]!.AsArray();
        eval.Items.Count.ShouldBe(rawFees.Count);
        for (var i = 0; i < rawFees.Count; i++)
        {
            eval.Items[i].FeeCode.ShouldBe(
                rawFees[i]!["feeCode"]!.GetValue<string>());
            eval.Items[i].Section.ShouldBe(
                rawFees[i]!["tridBucket"]?.GetValue<string>() ?? "");
            eval.Items[i].Total.ShouldBe(
                rawFees[i]!["total"]!.GetValue<decimal>());
        }
        eval.Fingerprint.ShouldBe(
            raw["fingerprint"]!.GetValue<string>());
    }
}
