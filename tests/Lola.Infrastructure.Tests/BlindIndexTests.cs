using Lola.Application.Security;
using Lola.Domain.Classification;
using Lola.Infrastructure.Crypto;
using Shouldly;

namespace Lola.Infrastructure.Tests;

[Collection("openbao")]
public sealed class BlindIndexTests(OpenBaoFixture fixture)
{
    [Fact]
    public async Task Index_IsDeterministic_AndNormalized()
    {
        var tenant = fixture.NewTenant();
        using var _ = CipherContext.Use(tenant);
        var km = new TransitKeyManager(fixture.Transit);
        await km.EnsureKeysAsync(tenant);
        try
        {
            var index = new TransitBlindIndex(fixture.Transit);
            var a = await index.IndexAsync(PiiClass.Ssn, "123-45-6789");
            var b = await index.IndexAsync(PiiClass.Ssn, "123456789"); // formatting differs, value same
            a.ShouldStartWith("idx:v1:");
            a.ShouldBe(b);
            a.ShouldNotContain("123456789");
        }
        finally { await km.DestroyAsync(tenant); }
    }

    [Fact]
    public async Task Index_IsTenantIsolated()
    {
        var a = fixture.NewTenant();
        var b = fixture.NewTenant();
        var km = new TransitKeyManager(fixture.Transit);
        await km.EnsureKeysAsync(a);
        await km.EnsureKeysAsync(b);
        try
        {
            var index = new TransitBlindIndex(fixture.Transit);
            string idxA, idxB;
            using (CipherContext.Use(a)) idxA = await index.IndexAsync(PiiClass.Ssn, "123-45-6789");
            using (CipherContext.Use(b)) idxB = await index.IndexAsync(PiiClass.Ssn, "123-45-6789");
            idxA.ShouldNotBe(idxB); // same SSN, different tenants → different HMAC keys
        }
        finally { await km.DestroyAsync(a); await km.DestroyAsync(b); }
    }

    [Fact]
    public async Task Contact_Normalizes_EmailAndPhone()
    {
        var tenant = fixture.NewTenant();
        using var _ = CipherContext.Use(tenant);
        var km = new TransitKeyManager(fixture.Transit);
        await km.EnsureKeysAsync(tenant);
        try
        {
            var index = new TransitBlindIndex(fixture.Transit);
            (await index.IndexAsync(PiiClass.Contact, "Person@Test.com"))
                .ShouldBe(await index.IndexAsync(PiiClass.Contact, "  person@test.COM "));
            (await index.IndexAsync(PiiClass.Contact, "(555) 123-4567"))
                .ShouldBe(await index.IndexAsync(PiiClass.Contact, "5551234567"));
        }
        finally { await km.DestroyAsync(tenant); }
    }
}
