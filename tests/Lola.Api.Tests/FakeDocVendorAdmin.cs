using Lola.Application.Documents;

namespace Lola.Api.Tests;

/// Hermetic IDocVendorAdmin for the API e2e (MPE-191): the republish
/// endpoint and the erasure revoke hook resolve the vendor-admin
/// seam — the fake records calls and simulates version bumps so the
/// handler logic runs without a live ISPPr admin session.
public sealed class FakeDocVendorAdmin : IDocVendorAdmin
{
    public List<string> Revoked { get; } = [];
    public List<IReadOnlyDictionary<string, string>> Republished
    { get; } = [];
    public int RepublishCalls;

    public Task<string> EnsureClientAsync(string name,
        CancellationToken ct = default)
        => Task.FromResult($"cli-{name}");

    public Task<string> EnsureCustomerAsync(string clientId, string name,
        string externalRef, CancellationToken ct = default)
        => Task.FromResult($"cust-{externalRef}");

    public Task<(string KeyId, string PlaintextKey)> CreateApiKeyAsync(
        string clientId, string label, IReadOnlyList<string> scopes,
        CancellationToken ct = default)
        => Task.FromResult(("key-fake", "isppr_fake_plaintext"));

    public Task<bool> UploadTemplateAsync(string clientId, string name,
        string html, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<IReadOnlyDictionary<string, long>>
        RepublishTemplatesAsync(string clientId,
            IReadOnlyDictionary<string, string> templates,
            CancellationToken ct = default)
    {
        RepublishCalls++;
        Republished.Add(templates);
        // Every call bumps every template one version — v2, v3, …
        var version = (long)RepublishCalls + 1;
        return Task.FromResult<IReadOnlyDictionary<string, long>>(
            templates.Keys.ToDictionary(n => n, _ => version));
    }

    public Task RevokeApiKeyAsync(string keyId,
        CancellationToken ct = default)
    {
        Revoked.Add(keyId);
        return Task.CompletedTask;
    }
}
