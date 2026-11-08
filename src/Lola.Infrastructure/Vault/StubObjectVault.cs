using System.Collections.Concurrent;
using Lola.Application.Tenancy;
using Lola.Application.Vault;

namespace Lola.Infrastructure.Vault;

/// Default object vault (MPE-200): deterministic in-memory store so
/// the upload → quarantine → scan flow is exercisable without MinIO.
/// `VAULT_ADAPTER=minio` swaps the real adapter in behind the same
/// interface. Scoped per ambient TenantContext — the stub honors the
/// same "cannot name another tenant's objects" shape as the real one,
/// and tests can read stored bytes back to prove ciphertext-at-rest.
public sealed class StubObjectVault : IObjectVault
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();

    public string Adapter => "stub";

    private string Scope() =>
        TenantContext.Current?.TenantId ?? "";

    /// Test-visible read — raw stored bytes (ciphertext in prod flow).
    public byte[]? ReadBytes(string key)
        => _objects.TryGetValue($"{Scope()}|{key}", out var b) ? b : null;

    /// Keys stored under a tenant scope — test assertions.
    public IReadOnlyList<string> KeysFor(string tenantId)
        => _objects.Keys.Where(k => k.StartsWith($"{tenantId}|",
                StringComparison.Ordinal))
            .Select(k => k[(tenantId.Length + 1)..]).ToList();

    public async Task PutAsync(string key, Stream content,
        CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        _objects[$"{Scope()}|{key}"] = ms.ToArray();
    }

    public Task<Stream> GetAsync(string key,
        CancellationToken ct = default)
        => Task.FromResult<Stream>(new MemoryStream(
            _objects[$"{Scope()}|{key}"]));

    public Task CopyAsync(string sourceKey, string destKey,
        CancellationToken ct = default)
    {
        _objects[$"{Scope()}|{destKey}"] = _objects[$"{Scope()}|{sourceKey}"];
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _objects.TryRemove($"{Scope()}|{key}", out _);
        return Task.CompletedTask;
    }

    public Task PurgeTenantAsync(CancellationToken ct = default)
    {
        foreach (var k in _objects.Keys
                     .Where(k => k.StartsWith($"{Scope()}|",
                         StringComparison.Ordinal)).ToList())
            _objects.TryRemove(k, out _);
        return Task.CompletedTask;
    }
}
