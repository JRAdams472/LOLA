using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Identity;

namespace Lola.Api.Tests;

/// Hermetic stand-ins for the provisioning crypto steps — the real
/// transit/SQLCipher path is proven in Lola.DataPlane.Tests against
/// live OpenBao; the API e2e only needs a wrapped-key-shaped value and
/// a file that exists.
internal sealed class FakeKeyManager : ITenantKeyManager
{
    public Task EnsureKeysAsync(string tenantId, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task RotateAsync(string tenantId, CancellationToken ct = default)
        => Task.CompletedTask;
    public Task<string> RewrapAsync(string tenantId, string ciphertext, CancellationToken ct = default)
        => Task.FromResult(ciphertext);
    public Task<IReadOnlyList<string>> RewrapAsync(
        string tenantId, IReadOnlyList<string> ciphertexts, CancellationToken ct = default)
        => Task.FromResult(ciphertexts);
    public Task DestroyAsync(string tenantId, CancellationToken ct = default)
        => Task.CompletedTask;
}

internal sealed class FakeTenantDbProvisioner(string dataDir) : ITenantDbProvisioner
{
    public Task<string> ProvisionFileAsync(TenantRecord tenant, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllBytes(Path.Combine(dataDir, $"{tenant.Id}.db"),
            new byte[] { 0x53, 0x51, 0x4c }); // marker bytes, not a real db
        return Task.FromResult($"vault:v1:test-wrapped-{tenant.Id}");
    }
}
