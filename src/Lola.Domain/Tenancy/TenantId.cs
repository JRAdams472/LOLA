using System.Text.RegularExpressions;

namespace Lola.Domain.Tenancy;

/// Tenant-id shape — the single choke point every tenant-id consumer
/// goes through before the id reaches a file path or an OpenBao key
/// path (`tenant-<id>`). DNS-label-style: lowercase alnum, interior
/// hyphens, ≤63 chars. Path separators, traversal, and uppercase can
/// never become key names or filenames.
public static class TenantId
{
    private static readonly Regex Pattern = new(
        @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && Pattern.IsMatch(value);

    /// Returns the id unchanged when valid; throws otherwise. Every
    /// interpolation site calls this — the check can't be forgotten.
    public static string Validate(string? value) =>
        IsValid(value)
            ? value!
            : throw new ArgumentException(
                "Tenant id must match ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$",
                nameof(value));
}
