using Lola.Application.Security;
using Lola.Domain.Classification;

namespace Lola.ControlPlane;

/// Cipher boundary for classified control-plane columns (MPE-286,
/// ADR-0004): repositories never touch plaintext outside these
/// helpers. Tenant-owned fields (users/invites/groups) ride the owning
/// tenant's transit key; control-plane-global fields (tenant registry
/// spine, OIDC rows, erasure tombstones — all must stay readable after
/// tenant keys are shredded) ride the `control-plane` key domain.
/// Blind indexes use the matching `<domain>-idx` hmac key.
internal static class CpCrypto
{
    public const string Domain = "control-plane";
    private const string VaultPrefix = "vault:";

    public static async Task<string?> EncryptAsync(
        IFieldCipher cipher, string tenantId, string? plaintext, CancellationToken ct)
    {
        if (plaintext is null) return null;
        using (CipherContext.Use(tenantId))
            return await cipher.EncryptAsync(plaintext, ct);
    }

    /// Tenant-keyed decrypt with a control-plane-domain retry: erasure
    /// tombstones are cp-keyed because the tenant key is already dead
    /// when residuals write (TenantErasureService destroys keys first).
    /// A live tenant-key value never reaches the fallback — the first
    /// decrypt succeeds.
    public static async Task<string?> DecryptTenantAsync(
        IFieldCipher cipher, string tenantId, string? value, CancellationToken ct)
    {
        var ciphertext = RequireCiphertext(value);
        if (ciphertext is null) return null;
        try
        {
            using (CipherContext.Use(tenantId))
                return await cipher.DecryptAsync(ciphertext, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return await DecryptGlobalAsync(cipher, ciphertext, ct);
        }
    }

    public static async Task<string?> EncryptGlobalAsync(
        IFieldCipher cipher, string? plaintext, CancellationToken ct)
    {
        if (plaintext is null) return null;
        using (CipherContext.UseDomain(Domain))
            return await cipher.EncryptAsync(plaintext, ct);
    }

    public static async Task<string?> DecryptGlobalAsync(
        IFieldCipher cipher, string? value, CancellationToken ct)
    {
        var ciphertext = RequireCiphertext(value);
        if (ciphertext is null) return null;
        using (CipherContext.UseDomain(Domain))
            return await cipher.DecryptAsync(ciphertext, ct);
    }

    public static async Task<string> TenantIndexAsync(
        IBlindIndex index, string tenantId, PiiClass cls, string value, CancellationToken ct)
    {
        using (CipherContext.Use(tenantId))
            return await index.IndexAsync(cls, value, ct);
    }

    public static async Task<string> GlobalIndexAsync(
        IBlindIndex index, PiiClass cls, string value, CancellationToken ct)
    {
        using (CipherContext.UseDomain(Domain))
            return await index.IndexAsync(cls, value, ct);
    }

    /// A classified column holds ciphertext or NULL — a bare value
    /// means an unconverted pre-encryption row and fails loud so the
    /// offline `cpconvert` step (MPE-287) owns the transition, never a
    /// silent plaintext passthrough.
    private static string? RequireCiphertext(string? value)
    {
        if (value is null) return null;
        return value.StartsWith(VaultPrefix, StringComparison.Ordinal)
            ? value
            : throw new InvalidOperationException(
                "Control-plane row carries a plaintext value in a classified " +
                "column — run tools/cpconvert before serving this database.");
    }
}
