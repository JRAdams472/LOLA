using Lola.Application.Errors;
using Lola.Application.Vault;

namespace Lola.Infrastructure.Vault;

/// Fail-closed IObjectVault + IVirusScanner for VAULT_ADAPTER=disabled
/// (MPE-277): the borrower-doc vault is explicitly off — every call
/// surfaces a 503, and erasure's PurgeTenantAsync throws rather than
/// silently skipping object cleanup.
public sealed class DisabledObjectVault : IObjectVault
{
    public string Adapter => "disabled";

    public Task PutAsync(string key, Stream content,
        CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "vault capability is disabled by configuration");

    public Task<Stream> GetAsync(string key,
        CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "vault capability is disabled by configuration");

    public Task CopyAsync(string sourceKey, string destKey,
        CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "vault capability is disabled by configuration");

    public Task DeleteAsync(string key, CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "vault capability is disabled by configuration");

    public Task PurgeTenantAsync(CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "vault capability is disabled by configuration");
}

/// Scanner side of the disabled vault — scans surface Unavailable,
/// never a silent Clean.
public sealed class DisabledScanner : IVirusScanner
{
    public string Adapter => "disabled";

    public Task<ScanVerdict> ScanAsync(Stream content,
        CancellationToken ct = default)
        => Task.FromResult(new ScanVerdict(ScanStatus.Unavailable,
            "vault capability is disabled by configuration"));
}
