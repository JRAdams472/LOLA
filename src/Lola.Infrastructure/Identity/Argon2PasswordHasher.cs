using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;
using Lola.Application.Identity;

namespace Lola.Infrastructure.Identity;

/// Argon2id password hashing (PHC-string format so parameters ride
/// with the hash). Parameters follow the RFC 9106 first
/// recommendation: m=64 MiB, t=3, p=4, 16-byte salt, 32-byte tag.
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    // Config carries the password + fresh salt on the object — new
    // instance per call so the hasher stays stateless/thread-safe.
    // EncodeString needs the salt set up front; Isopoh does not
    // generate one inside Hash() when building the PHC string.
    public string Hash(string password) => Argon2.Hash(new Argon2Config
    {
        Type = Argon2Type.HybridAddressing, // Argon2id
        TimeCost = 3,
        MemoryCost = 65536, // KiB → 64 MiB
        Lanes = 4,
        Threads = 4,
        HashLength = 32,
        Salt = RandomNumberGenerator.GetBytes(16),
        Password = Encoding.UTF8.GetBytes(password)
    });

    /// Verify parses parameters from the PHC string itself — the
    /// caller's config doesn't matter, so the plain password overload
    /// is the right shape.
    public bool Verify(string password, string passwordHash) =>
        Argon2.Verify(passwordHash, password);
}
