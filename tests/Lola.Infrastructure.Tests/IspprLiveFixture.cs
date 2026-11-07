using Lola.Application.Documents;
using Lola.Infrastructure.Documents;

namespace Lola.Infrastructure.Tests;

/// Live ISPPr stack (MPE-190): the real dge-api on :8090 with the
/// seeded demo tenant — same never-skip contract as the OpenBao,
/// MPEx, and Zara fixtures. Required env: ISPPR_BASE_URL,
/// ISPPR_API_KEY (the seed's generate-scoped key),
/// ISPPR_ADMIN_EMAIL/ISPPR_ADMIN_PASSWORD (session-login provisioning
/// proof). ISPPR_CUSTOMER_ID defaults to the seed's cust_demo.
[CollectionDefinition("isppr")]
public sealed class IspprLiveFixture : ICollectionFixture<IspprLiveFixture>
{
    public IspprDocOptions Doc { get; }
    public IspprAdminOptions Admin { get; }
    public string ApiKey { get; }
    public string CustomerId { get; }

    public IspprLiveFixture()
    {
        string Require(string key) =>
            Environment.GetEnvironmentVariable(key)
            ?? throw new InvalidOperationException(
                $"{key} is required — these tests prove the ISPPr " +
                "round-trip against the live engine and never skip.");

        Doc = new IspprDocOptions(Require("ISPPR_BASE_URL"));
        Admin = new IspprAdminOptions(Doc.BaseUrl,
            Require("ISPPR_ADMIN_EMAIL"), Require("ISPPR_ADMIN_PASSWORD"));
        ApiKey = Require("ISPPR_API_KEY");
        CustomerId = Environment.GetEnvironmentVariable(
            "ISPPR_CUSTOMER_ID") ?? "cust_demo";
    }

    /// Adapter over a real redirect-non-following client — the same
    /// handler shape Program.cs registers.
    public IspprDocumentAdapter Adapter(string? apiKey = null)
    {
        var http = new HttpClient(
            new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };
        return new IspprDocumentAdapter(http, Doc,
            new StaticCredentials(apiKey ?? ApiKey));
    }

    public IspprSessionClient AdminClient()
        => new(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            Admin);

    private sealed class StaticCredentials(string key)
        : IDocumentCredentialSource
    {
        public Task<string?> ApiKeyAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(key);
    }
}
