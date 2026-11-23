using Lola.Infrastructure.OpenBao;

namespace Lola.Api.Identity;

/// Resolves the control-plane SQLCipher file key at startup
/// (MPE-276 / Phase 7b, design docs/control-plane-encryption-design.md).
/// Accepted forms — resolved in order:
///   CONTROL_DB_KEY      wrapped `vault:vN:…` blob → transit-decrypted
///                       under the `control-plane` key domain, or a raw
///                       64-hex key (development only);
///   CONTROL_DB_KEY_FILE path to a file holding either form.
/// `null` means an intentionally unkeyed file (transitional dev /
/// conversion source). Production with no configured key throws —
/// the control plane never opens unkeyed there.
public static class ControlPlaneKeyResolver
{
    public static string? Resolve(IConfiguration config,
        OpenBaoTransitClient? transit, bool isProduction)
    {
        var raw = config["CONTROL_DB_KEY"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            var file = config["CONTROL_DB_KEY_FILE"];
            if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
                raw = File.ReadAllText(file).Trim();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            if (isProduction)
                throw new InvalidOperationException(
                    "CONTROL_DB_KEY (or CONTROL_DB_KEY_FILE) is required in " +
                    "Production — the control-plane file never opens unkeyed. " +
                    "Provision a wrapped blob under transit/keys/control-plane.");
            return null;
        }

        if (raw.StartsWith("vault:", StringComparison.Ordinal))
        {
            if (transit is null)
                throw new InvalidOperationException(
                    "CONTROL_DB_KEY is a wrapped transit blob but OpenBao is not " +
                    "configured — set OPENBAO_ADDR and a scoped token.");
            // Wrapped value convention matches TenantDbProvisioner:
            // plaintext is the base64 of the 32-byte file key.
            var b64 = transit.DecryptAsync("control-plane", raw)
                .GetAwaiter().GetResult();
            return Convert.ToHexString(Convert.FromBase64String(b64));
        }

        if (raw.Length != 64 || !raw.All(Uri.IsHexDigit))
            throw new InvalidOperationException(
                "CONTROL_DB_KEY must be a `vault:` wrapped blob or a raw " +
                "64-hex file key (development only) — got neither.");

        return raw.ToUpperInvariant();
    }
}
