namespace Lola.Application.Security;

/// Ambient tenant scope for crypto operations. Dapper parameter
/// emission and repository reads can't thread per-call tenant state,
/// so the resolved tenant rides an AsyncLocal — set once per request by
/// tenant-resolution middleware (M4) or explicitly by callers/tests.
public static class CipherContext
{
    private static readonly AsyncLocal<string?> _tenantId = new();

    public static string? TenantId => _tenantId.Value;

    /// Current tenant or throw — crypto without a tenant is a bug,
    /// never a default.
    public static string RequiredTenantId =>
        _tenantId.Value ?? throw new InvalidOperationException(
            "No tenant in CipherContext — tenant resolution must run before crypto operations.");

    /// Open a tenant scope; dispose to restore the previous value.
    /// Safe to nest and safe across async continuations (AsyncLocal).
    public static IDisposable Use(string tenantId)
    {
        var previous = _tenantId.Value;
        _tenantId.Value = tenantId;
        return new Pop(() => _tenantId.Value = previous);
    }

    private sealed class Pop(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
