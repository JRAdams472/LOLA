using System.Security.Cryptography;
using System.Text;

namespace Lola.Api.Vendors;

/// Webhook ingress authentication (MPE-228): `X-Vendor-Signature:
/// sha256=<hex>` is HMAC-SHA256 over the raw body keyed by the
/// tenant's `webhook_secret` from OpenBao KV — the only vendor-auth
/// channel; vendors hold no OIDC tokens.
public static class VendorWebhookAuth
{
    public const string SignatureHeader = "X-Vendor-Signature";
    private const string Prefix = "sha256=";

    public static bool Verify(string? header, byte[] body, string secret)
    {
        if (header is null ||
            !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var sent = header[Prefix.Length..].Trim();
        var expected = Convert.ToHexString(
            HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(secret), body))
            .ToLowerInvariant();
        // FixedTimeEquals — signature compare must not leak timing.
        return sent.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(sent),
                Encoding.ASCII.GetBytes(expected));
    }
}
