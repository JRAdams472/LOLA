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

/// API tests have no OpenBao — the provisioning pipeline's migrate
/// step is faked to success (the real runner is proven against real
/// SQLCipher + real OpenBao in Lola.DataPlane.Tests).
internal sealed class FakeMigrationRunner : ITenantMigrationRunner
{
    public Task<TenantMigrationResult> MigrateAsync(
        TenantRecord tenant, CancellationToken ct = default)
        => Task.FromResult(new TenantMigrationResult(tenant.Id, true, ["0001_baseline"], null));
    public Task<IReadOnlyList<TenantMigrationResult>> MigrateAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TenantMigrationResult>>([]);
}

/// Hermetic backup/restore stand-in — the real VACUUM INTO + keyed
/// restore path is proven in Lola.DataPlane.Tests against real
/// SQLCipher + OpenBao; the API e2e only exercises the routes.
internal sealed class FakeTenantBackup : ITenantBackup
{
    private static readonly TenantBackupInfo[] Infos =
        [new("t", "20261028T000000000Z.db", "2026-10-28T00:00:00.000Z", "ab12", 1024)];

    public Task<TenantBackupInfo> BackupAsync(TenantRecord tenant, CancellationToken ct = default)
        => Task.FromResult(Infos[0] with { TenantId = tenant.Id });
    public Task<IReadOnlyList<TenantBackupInfo>> ListAsync(string tenantId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TenantBackupInfo>>(
            [Infos[0] with { TenantId = tenantId }]);
    public Task<TenantBackupInfo> RestoreAsync(TenantRecord tenant, string name, CancellationToken ct = default)
        => Task.FromResult(Infos[0] with { TenantId = tenant.Id, Name = name });
}

/// Hermetic erasure stand-in — real crypto-shredding is proven in
/// Lola.DataPlane.Tests; the API e2e only exercises the route guards.
internal sealed class FakeTenantEraser : ITenantEraser
{
    public Task<TenantErasureResult> EraseAsync(
        TenantRecord tenant, CancellationToken ct = default)
        => Task.FromResult(new TenantErasureResult(tenant.Id, 3));
}
