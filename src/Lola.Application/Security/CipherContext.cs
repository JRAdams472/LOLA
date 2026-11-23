namespace Lola.Application.Security;

/// Ambient tenant scope for crypto operations. Dapper parameter
/// emission and repository reads can't thread per-call tenant state,
/// so the resolved tenant rides an AsyncLocal — set once per request by
/// tenant-resolution middleware (M4) or explicitly by callers/tests.
public static class CipherContext
{
    private static readonly AsyncLocal<string?> _tenantId = new();
    private static readonly AsyncLocal<string?> _domain = new();

    public static string? TenantId => _tenantId.Value;

    /// Explicit transit key domain override (e.g. `control-plane` for
    /// control-plane-global fields that have no owning tenant). When
    /// set it wins over the tenant-derived key name — control-plane
    /// code scopes calls with <see cref="UseDomain"/> rather than
    /// faking a tenant id.
    public static string? Domain => _domain.Value;

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

    /// Open an explicit key-domain scope; dispose to restore.
    public static IDisposable UseDomain(string domain)
    {
        var previous = _domain.Value;
        _domain.Value = domain;
        return new Pop(() => _domain.Value = previous);
    }

    private sealed class Pop(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
