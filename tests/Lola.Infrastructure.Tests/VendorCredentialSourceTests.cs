using Lola.Application.Tenancy;
using Lola.Application.Vendors;
using Lola.Infrastructure.OpenBao;
using Lola.Infrastructure.Vendors;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// MPE-226 credential proof (invariant 7, ticket: "creds never in
/// config/DB/repo"): the ONLY resolution path for vendor credential
/// material is OpenBao KV — a real `lola-kv` mount on the live
/// fixture. Write via the KV client, read through the port under a
/// TenantContext, assert the material round-trips and a missing kind
/// resolves null rather than a fallback.
[Collection("openbao")]
public sealed class VendorCredentialSourceTests(OpenBaoFixture fixture)
{
    private OpenBaoKvClient Kv() => new(new HttpClient(),
        Options.Create(new OpenBaoOptions
        {
            Address = Environment.GetEnvironmentVariable("OPENBAO_ADDR"),
            Token = Environment.GetEnvironmentVariable("OPENBAO_TOKEN")
        }));

    [Fact]
    public async Task Credentials_round_trip_through_kv_only()
    {
        var kv = Kv();
        // Root-token mount ensure — CI's OpenBao starts bare; compose
        // dev already has lola-kv via openbao-init (both are no-ops
        // when the mount exists).
        await kv.EnsureMountAsync();
        var tenant = fixture.NewTenant();
        var path = $"vendors/{tenant}/credit";
        await kv.WriteAsync(path, new Dictionary<string, string>
        {
            ["api_key"] = "test-api-key-abc",
            ["webhook_secret"] = "test-webhook-def"
        });
        try
        {
            using var _ = TenantContext.Use(
                new TenantScope(tenant, "/unused.db", null));
            var creds = await new OpenBaoVendorCredentialSource(kv)
                .ForAsync(VendorKind.Credit);

            creds.ShouldNotBeNull();
            creds!.ApiKey.ShouldBe("test-api-key-abc");
            creds.WebhookSecret.ShouldBe("test-webhook-def");

            // A kind with no KV document resolves null — the adapter's
            // signal for "tenant not bound", never a shared fallback.
            (await new OpenBaoVendorCredentialSource(kv)
                .ForAsync(VendorKind.Fraud)).ShouldBeNull();
        }
        finally
        {
            await kv.DeleteAsync(path);
        }
    }
}
