using Lola.Domain.Tenancy;
using Shouldly;

namespace Lola.Domain.Tests;

/// M2 hardening F1: tenant ids reach OpenBao key paths and filesystem
/// paths — the charset must make injection/traversal impossible.
public class TenantIdTests
{
    [Theory]
    [InlineData("acme")]
    [InlineData("acme-1")]
    [InlineData("a")]
    [InlineData("0")]
    [InlineData("tenant-9x")]
    public void Valid_ids_accepted(string id) => TenantId.IsValid(id).ShouldBeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Acme")]            // uppercase
    [InlineData("-acme")]           // leading hyphen
    [InlineData("acme-")]           // trailing hyphen
    [InlineData("acme corp")]       // space
    [InlineData("acme_corp")]       // underscore
    [InlineData("../escape")]       // traversal
    [InlineData("a/b")]             // separator
    [InlineData("a\\b")]            // separator
    [InlineData("a;drop")]          // injection
    [InlineData("a'b")]             // quote
    [InlineData("tenant-.")]        // dot
    public void Unsafe_ids_rejected(string? id) => TenantId.IsValid(id).ShouldBeFalse();

    [Fact]
    public void Over_63_chars_rejected()
        => TenantId.IsValid(new string('a', 64)).ShouldBeFalse();

    [Fact]
    public void Exactly_63_chars_accepted()
        => TenantId.IsValid(new string('a', 63)).ShouldBeTrue();

    [Fact]
    public void Validate_returns_id_or_throws()
    {
        TenantId.Validate("acme").ShouldBe("acme");
        Should.Throw<ArgumentException>(() => TenantId.Validate("../nope"));
    }
}
