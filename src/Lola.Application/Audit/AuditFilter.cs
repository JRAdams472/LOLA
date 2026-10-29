namespace Lola.Application.Audit;

/// Filter for audit queries (MPE-117 access-review reports). Every
/// member is optional; null means "don't filter on it". `Since`/`Until`
/// bound `created_utc` — ISO-8601 Z strings compare lexicographically.
public sealed record AuditFilter(
    string? Entity = null, string? EntityId = null, string? Actor = null,
    string? Action = null, string? Since = null, string? Until = null);
