using Lola.Infrastructure.Fees;

namespace Lola.Infrastructure.Tests;

/// Live Zara stack (MPE-183): real engine on :8400 with a devtoken
/// minted into a token file — same never-skip contract as OpenBao
/// and MPEx fixtures. Required env: ZARA_BASE_URL, ZARA_ENV,
/// ZARA_TENANT_ID, and ZARA_TOKEN_FILE or ZARA_TOKEN.
[CollectionDefinition("zara")]
public sealed class ZaraLiveFixture : ICollectionFixture<ZaraLiveFixture>
{
    public ZaraFeeOptions Options { get; }

    public ZaraLiveFixture()
    {
        string Require(string key) =>
            Environment.GetEnvironmentVariable(key)
            ?? throw new InvalidOperationException(
                $"{key} is required — these tests prove the Zara " +
                "round-trip against the live engine and never skip.");

        Options = new ZaraFeeOptions(
            Require("ZARA_BASE_URL"),
            Environment.GetEnvironmentVariable("ZARA_ENV") ?? "dev",
            Require("ZARA_TENANT_ID"),
            Environment.GetEnvironmentVariable("ZARA_TOKEN_FILE"),
            Environment.GetEnvironmentVariable("ZARA_TOKEN"));
        if (Options.TokenFile is null && Options.Token is null)
            throw new InvalidOperationException(
                "ZARA_TOKEN_FILE or ZARA_TOKEN is required — these " +
                "tests prove the Zara round-trip and never skip.");
    }
}
