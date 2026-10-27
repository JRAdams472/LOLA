using Lola.Domain.Identity;

namespace Lola.Application.Tenancy;

/// Resolved tenant for the current request — the ONLY object allowed
/// to describe which tenant file a data-plane connection opens. Set
/// once per request by tenant-resolution middleware (M3 Phase 3) or
/// explicitly by provisioning/tests. There is deliberately no API
/// that takes a raw tenant id at query time: the connection factory
/// reads this scope, so no code path can name another tenant's file.
public sealed record TenantScope(string TenantId, string DbPath, string DbKeyWrapped)
{
    public static TenantScope From(TenantRecord tenant) =>
        new(tenant.Id, tenant.DbPath,
            tenant.DbKeyWrapped ?? throw new InvalidOperationException(
                $"Tenant '{tenant.Id}' has no data-plane key — provisioning is incomplete."));
}

public static class TenantContext
{
    private static readonly AsyncLocal<TenantScope?> _scope = new();

    public static TenantScope? Current => _scope.Value;

    public static TenantScope Required =>
        _scope.Value ?? throw new InvalidOperationException(
            "No tenant in TenantContext — tenant resolution must run before data-plane access.");

    /// Open a tenant scope; dispose to restore the previous value.
    /// Mirrors CipherContext.Use — middleware sets both together.
    public static IDisposable Use(TenantScope scope)
    {
        var previous = _scope.Value;
        _scope.Value = scope;
        return new Pop(() => _scope.Value = previous);
    }

    private sealed class Pop(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
