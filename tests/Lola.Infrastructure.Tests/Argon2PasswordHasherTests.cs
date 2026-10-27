using Lola.Infrastructure.Identity;
using Shouldly;

namespace Lola.Infrastructure.Tests;

public class Argon2PasswordHasherTests
{
    private readonly Argon2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_produces_argon2id_phc_string()
    {
        var hash = _hasher.Hash("correct horse battery staple");
        hash.ShouldStartWith("$argon2id$");
        hash.ShouldContain("m=65536");
        hash.ShouldContain("t=3");
        hash.ShouldContain("p=4");
    }

    [Fact]
    public void Verify_accepts_correct_password()
        => _hasher.Verify("hunter2", _hasher.Hash("hunter2")).ShouldBeTrue();

    [Fact]
    public void Verify_rejects_wrong_password()
        => _hasher.Verify("hunter3", _hasher.Hash("hunter2")).ShouldBeFalse();

    [Fact]
    public void Hash_is_salted_per_call()
        => _hasher.Hash("same").ShouldNotBe(_hasher.Hash("same"));
}
