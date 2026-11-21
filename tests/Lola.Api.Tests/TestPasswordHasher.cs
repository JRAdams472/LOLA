using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;
using Lola.Application.Identity;

namespace Lola.Api.Tests;

/// Test-profile Argon2id (MPE-269) — the real algorithm at a minimal
/// work factor. Parameters ride the PHC string, so every hash this
/// produces verifies fast too; `Argon2PasswordHasher` and its RFC 9106
/// parameters never reach the test host. The api suite mints ~124
/// PKCE logins — this is the difference between ~300ms and ~15ms each.
public sealed class TestPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => Argon2.Hash(new Argon2Config
    {
        Type = Argon2Type.HybridAddressing,
        TimeCost = 1,
        MemoryCost = 8192, // KiB → 8 MiB
        Lanes = 1,
        Threads = 1,
        HashLength = 32,
        Salt = RandomNumberGenerator.GetBytes(16),
        Password = Encoding.UTF8.GetBytes(password)
    });

    /// Verify parses parameters out of the stored PHC string — cost
    /// follows whatever hash it is given, which here is always weak.
    public bool Verify(string password, string passwordHash) =>
        Argon2.Verify(passwordHash, password);
}
