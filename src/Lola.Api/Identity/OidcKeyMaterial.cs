using System.Security.Cryptography;
using System.Text.Json;
using Lola.Infrastructure.OpenBao;
using Microsoft.IdentityModel.Tokens;

namespace Lola.Api.Identity;

/// Strict OIDC key resolver (MPE-277, security-remediation phase 8).
/// Unlike <see cref="DevSigningKey"/> this NEVER creates a key — the
/// configured source must exist and parse. Accepted forms:
///   {KEY}      PEM (PKCS8/PKCS1) or RSAParameters JSON, or a
///              `vault:vN:…` transit blob whose plaintext is either;
///   {KEY}_FILE path to a file holding any of the above.
/// `vault:` decryption uses the `oidc` transit key domain — provision
/// it once (`bao write -f lola-transit/keys/oidc`, see
/// docs/deployment-runbook.md). The KeyId is a digest of the modulus so
/// rotating material changes the kid published to clients.
public static class OidcKeyMaterial
{
    /// True when the deployment supplied protected key material —
    /// Program.cs prefers this over the dev auto-create path.
    public static bool Configured(IConfiguration config, string key)
        => !string.IsNullOrWhiteSpace(config[key])
            || !string.IsNullOrWhiteSpace(config[$"{key}_FILE"]);

    public static RsaSecurityKey Resolve(IConfiguration config,
        OpenBaoTransitClient? transit, string key)
    {
        var raw = config[key];
        var file = config[$"{key}_FILE"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (string.IsNullOrWhiteSpace(file))
                throw new InvalidOperationException(
                    $"{key} or {key}_FILE is required");
            if (!File.Exists(file))
                throw new InvalidOperationException(
                    $"{key}_FILE points at '{file}' which does not exist");
            raw = File.ReadAllText(file).Trim();
        }

        if (raw.StartsWith("vault:", StringComparison.Ordinal))
        {
            if (transit is null)
                throw new InvalidOperationException(
                    $"{key} is a wrapped transit blob but OpenBao is not " +
                    "configured — set OPENBAO_ADDR and a scoped token.");
            raw = transit.DecryptAsync("oidc", raw)
                .GetAwaiter().GetResult().Trim();
        }

        var rsa = Parse(raw, key);
        // kid derived from key material — a rotation publishes a NEW kid,
        // so clients honoring the JWKS never confuse old/new keys.
        var kid = Convert.ToHexString(
                SHA256.HashData(rsa.Modulus!))[..12].ToLowerInvariant();
        return new RsaSecurityKey(rsa) { KeyId = $"lola-{kid}" };
    }

    private static RSAParameters Parse(string raw, string key)
    {
        if (raw.StartsWith("-----BEGIN", StringComparison.Ordinal))
        {
            using var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(raw);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"{key} is not a loadable PEM RSA key", ex);
            }
            var p = rsa.ExportParameters(includePrivateParameters: true);
            if (p.D is null or { Length: 0 })
                throw new InvalidOperationException(
                    $"{key} must carry an RSA PRIVATE key — signing and " +
                    "encryption both need private material");
            return p;
        }

        try
        {
            var p = JsonSerializer.Deserialize<RSAParameters>(raw,
                new JsonSerializerOptions { IncludeFields = true });
            if (p.Modulus is null or { Length: 0 }
                || p.D is null or { Length: 0 })
                throw new InvalidOperationException(
                    $"{key} parsed as RSAParameters but is missing " +
                    "private material");
            return p;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{key} is neither PEM nor RSAParameters JSON", ex);
        }
    }
}
