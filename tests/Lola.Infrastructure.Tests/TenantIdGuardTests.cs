using Lola.Infrastructure.Crypto;
using Lola.Infrastructure.OpenBao;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// M2 hardening F1 — an unsafe tenant id must never reach a transit
/// key path. Validation fires inside the key-name helpers, before any
/// HTTP call, so this suite needs no live OpenBao.
public class TenantIdGuardTests
{
    private static TransitKeyManager Km() => new(
        new OpenBaoTransitClient(new HttpClient(),
            Options.Create(new OpenBaoOptions())));

    [Theory]
    [InlineData("../escape")]
    [InlineData("UPPER")]
    [InlineData("a/b")]
    [InlineData("a;b")]
    public async Task Unsafe_ids_throw_before_key_creation(string badId)
    {
        var km = Km();
        await Should.ThrowAsync<ArgumentException>(() => km.EnsureKeysAsync(badId));
        await Should.ThrowAsync<ArgumentException>(() => km.RotateAsync(badId));
        await Should.ThrowAsync<ArgumentException>(() => km.DestroyAsync(badId));
        await Should.ThrowAsync<ArgumentException>(() => km.RewrapAsync(badId, "vault:v1:x"));
    }
}
