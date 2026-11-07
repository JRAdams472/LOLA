using Lola.Application.Documents;

namespace Lola.Infrastructure.Documents;

/// Fail-closed IDocVendorAdmin for environments without ISPPr admin
/// creds (MPE-191): registered whenever ISPPR_ADMIN_* is absent so
/// DI always resolves — every call surfaces Unavailable instead of a
/// silent no-op. That keeps the vendor side honest: a tenant with a
/// live vendor key can't be erased quietly, and republish reports
/// the missing configuration rather than pretending it ran.
public sealed class MissingDocVendorAdmin : IDocVendorAdmin
{
    private static DocAdapterException Missing()
        => DocAdapterException.Unavailable(
            "ISPPr admin session is not configured " +
            "(ISPPR_ADMIN_EMAIL/ISPPR_ADMIN_PASSWORD)");

    public Task<string> EnsureClientAsync(string name,
        CancellationToken ct = default) => throw Missing();

    public Task<string> EnsureCustomerAsync(string clientId, string name,
        string externalRef, CancellationToken ct = default)
        => throw Missing();

    public Task<(string KeyId, string PlaintextKey)> CreateApiKeyAsync(
        string clientId, string label, IReadOnlyList<string> scopes,
        CancellationToken ct = default) => throw Missing();

    public Task<bool> UploadTemplateAsync(string clientId, string name,
        string html, CancellationToken ct = default) => throw Missing();

    public Task<IReadOnlyDictionary<string, long>>
        RepublishTemplatesAsync(string clientId,
            IReadOnlyDictionary<string, string> templates,
            CancellationToken ct = default) => throw Missing();

    public Task RevokeApiKeyAsync(string keyId,
        CancellationToken ct = default) => throw Missing();
}
