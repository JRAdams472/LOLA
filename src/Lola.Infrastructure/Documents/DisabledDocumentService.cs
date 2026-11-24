using Lola.Application.Errors;
using Lola.Application.Documents;

namespace Lola.Infrastructure.Documents;

/// Fail-closed IDocumentService for DOC_ADAPTER=disabled (MPE-277):
/// the doc-engine capability is explicitly off — every call surfaces
/// a 503 rather than simulated package results.
public sealed class DisabledDocumentService : IDocumentService
{
    public string Adapter => "disabled";

    public Task<DocGeneration> GenerateAsync(DocPackageRequest request,
        CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "document capability is disabled by configuration");

    public Task<DocGeneration> GetStatusAsync(string requestId,
        CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "document capability is disabled by configuration");

    public Task<string> GetDownloadUrlAsync(string requestId,
        CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "document capability is disabled by configuration");
}

/// Fail-closed IDocVendorAdmin for DOC_ADAPTER=disabled — erasure /
/// republish must never silently skip the vendor side, so the call
/// surfaces the disabled state instead of a missing-credential one.
public sealed class DisabledDocVendorAdmin : IDocVendorAdmin
{
    private static DocAdapterException Disabled()
        => DocAdapterException.Unavailable(
            "document capability is disabled by configuration");

    public Task<string> EnsureClientAsync(string name,
        CancellationToken ct = default) => throw Disabled();

    public Task<string> EnsureCustomerAsync(string clientId, string name,
        string externalRef, CancellationToken ct = default)
        => throw Disabled();

    public Task<(string KeyId, string PlaintextKey)> CreateApiKeyAsync(
        string clientId, string label, IReadOnlyList<string> scopes,
        CancellationToken ct = default) => throw Disabled();

    public Task<bool> UploadTemplateAsync(string clientId, string name,
        string html, CancellationToken ct = default) => throw Disabled();

    public Task<IReadOnlyDictionary<string, long>>
        RepublishTemplatesAsync(string clientId,
            IReadOnlyDictionary<string, string> templates,
            CancellationToken ct = default) => throw Disabled();

    public Task RevokeApiKeyAsync(string keyId,
        CancellationToken ct = default) => throw Disabled();
}
