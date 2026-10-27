using Lola.Application.Security;
using Lola.Infrastructure.Crypto;
using Lola.Infrastructure.OpenBao;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// Acceptance: round-trip encrypt/decrypt per tenant; rotation rewraps;
/// destroyed key ⇒ unrecoverable.
[Collection("openbao")]
public sealed class FieldCipherTests(OpenBaoFixture fixture)
{
    [Fact]
    public async Task EncryptDecrypt_RoundTrips_PerTenant()
    {
        var tenant = fixture.NewTenant();
        using var _ = CipherContext.Use(tenant);
        var km = new TransitKeyManager(fixture.Transit);
        await km.EnsureKeysAsync(tenant);
        try
        {
            var cipher = new TransitFieldCipher(fixture.Transit);
            var ct = await cipher.EncryptAsync("123-45-6789");
            ct.ShouldStartWith("vault:v1:");
            ct.ShouldNotContain("123-45-6789");
            (await cipher.DecryptAsync(ct)).ShouldBe("123-45-6789");
        }
        finally { await km.DestroyAsync(tenant); }
    }

    [Fact]
    public async Task Ciphertext_IsTenantIsolated()
    {
        var a = fixture.NewTenant();
        var b = fixture.NewTenant();
        var km = new TransitKeyManager(fixture.Transit);
        await km.EnsureKeysAsync(a);
        await km.EnsureKeysAsync(b);
        try
        {
            var cipher = new TransitFieldCipher(fixture.Transit);
            string ct;
            using (CipherContext.Use(a)) ct = await cipher.EncryptAsync("secret");
            using (CipherContext.Use(b))
                await Should.ThrowAsync<OpenBaoException>(cipher.DecryptAsync(ct));
        }
        finally { await km.DestroyAsync(a); await km.DestroyAsync(b); }
    }

    [Fact]
    public async Task Batch_RoundTrips()
    {
        var tenant = fixture.NewTenant();
        using var _ = CipherContext.Use(tenant);
        var km = new TransitKeyManager(fixture.Transit);
        await km.EnsureKeysAsync(tenant);
        try
        {
            var cipher = new TransitFieldCipher(fixture.Transit);
            var inputs = new[] { "alpha", "beta", "gamma" };
            var cts = await cipher.EncryptAsync(inputs);
            cts.Count.ShouldBe(3);
            cts.ShouldAllBe(c => c.StartsWith("vault:v1:"));
            (await cipher.DecryptAsync(cts)).ShouldBe(inputs);
        }
        finally { await km.DestroyAsync(tenant); }
    }

    [Fact]
    public async Task Rotate_ThenRewrap_AdvancesVersion_AndOldStillDecrypts()
    {
        var tenant = fixture.NewTenant();
        using var _ = CipherContext.Use(tenant);
        var km = new TransitKeyManager(fixture.Transit);
        await km.EnsureKeysAsync(tenant);
        try
        {
            var cipher = new TransitFieldCipher(fixture.Transit);
            var v1 = await cipher.EncryptAsync("borrower-secret");

            await km.RotateAsync(tenant);
            var v2 = await cipher.EncryptAsync("borrower-secret");
            v2.ShouldStartWith("vault:v2:");
            (await cipher.DecryptAsync(v1)).ShouldBe("borrower-secret"); // old still reads

            var rewrapped = await km.RewrapAsync(tenant, v1);
            rewrapped.ShouldStartWith("vault:v2:");
            (await cipher.DecryptAsync(rewrapped)).ShouldBe("borrower-secret");
        }
        finally { await km.DestroyAsync(tenant); }
    }

    [Fact]
    public async Task Destroy_MakesCiphertext_Unrecoverable()
    {
        var tenant = fixture.NewTenant();
        using var _ = CipherContext.Use(tenant);
        var km = new TransitKeyManager(fixture.Transit);
        await km.EnsureKeysAsync(tenant);
        var cipher = new TransitFieldCipher(fixture.Transit);
        var ct = await cipher.EncryptAsync("gone-forever");

        await km.DestroyAsync(tenant); // crypto-shredding — no finally, this IS the end state

        var ex = await Should.ThrowAsync<OpenBaoException>(cipher.DecryptAsync(ct));
        ex.Message.ShouldNotContain("gone-forever"); // no plaintext in errors either
    }
}
