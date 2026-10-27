using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace Lola.Api.Identity;

/// Dev signing key for OpenIddict — an RSA key generated once and
/// persisted under gitignored `data/oidc/` (symmetric to
/// `data/openbao/`). Restarts keep validating issued tokens. Real
/// deployments swap this for a managed key source (documented in the
/// identity runbook).
public static class DevSigningKey
{
    public static RsaSecurityKey LoadOrCreate(
        string path, string keyId = "lola-dev-signing")
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // RSAParameters uses fields, not properties — IncludeFields
        // is required or the round-trip comes back all-null.
        var options = new JsonSerializerOptions { IncludeFields = true };
        RSAParameters? p = TryRead();

        // A stale or truncated file deserializes to null fields —
        // regenerate rather than crash with "Modulus is null". The
        // write goes through a temp file + atomic move so parallel
        // hosts (e.g. concurrent test factories) converge on the
        // first writer's key instead of colliding mid-write.
        if (p?.Modulus is null or { Length: 0 })
        {
            using var rsa = RSA.Create(2048);
            p = rsa.ExportParameters(includePrivateParameters: true);
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(p.Value, options));
            try
            {
                File.Move(tmp, path);
            }
            catch (IOException)
            {
                // Another process won the create race — use its key.
                TryDelete(tmp);
                p = TryRead() ?? p;
            }
        }

        return new RsaSecurityKey(p.Value) { KeyId = keyId };

        RSAParameters? TryRead()
        {
            try
            {
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<RSAParameters>(File.ReadAllText(path), options)
                    : null;
            }
            catch
            {
                // Unreadable/corrupt dev key file — treated as absent.
                return null;
            }
        }

        static void TryDelete(string file)
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
