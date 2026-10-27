using Lola.Domain.Classification;

namespace Lola.Application.Identity;

/// Ambient PII-visibility scope for the current request — the set of
/// PII classes the principal may see in plaintext (ADR-0008: absence =
/// masked). Set once per request by tenant-resolution middleware from
/// the user's union of group roles; the masking post-processor reads it.
/// Ambient for the same reason CipherContext is — Dapper/handlers can't
/// thread per-request visibility through every signature.
public static class VisibilityContext
{
    private static readonly AsyncLocal<IReadOnlySet<PiiClass>?> _visible = new();
    private static readonly IReadOnlySet<PiiClass> Empty = new HashSet<PiiClass>();

    /// Classes visible in plaintext for the current scope. Defaults to
    /// empty — masked-by-default when no scope is set at all.
    public static IReadOnlySet<PiiClass> Current => _visible.Value ?? Empty;

    /// Open a visibility scope; dispose to restore the previous value.
    public static IDisposable Use(IReadOnlySet<PiiClass> visible)
    {
        var previous = _visible.Value;
        _visible.Value = visible;
        return new Pop(() => _visible.Value = previous);
    }

    private sealed class Pop(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
