using Lola.Domain.Applications;

namespace Lola.Application.Audit;

/// Field-class extraction for audit events (invariant 3: classes, never
/// values). The catalog is the source of truth — a leaf counts only
/// when its spec carries a piiClass.
public static class AuditFields
{
    private static readonly FieldCatalog Catalog = FieldCatalog.Current;

    /// Distinct PII classes present in a document — for read/export/
    /// submit events where the whole doc is touched.
    public static IReadOnlyList<string> InDoc(ApplicationDocument doc)
        => doc.Leaves()
            .Select(l => Catalog.SpecFor(l.Path)?.PiiClass?.ToString())
            .Where(c => c is not null).Cast<string>()
            .Distinct(StringComparer.Ordinal).OrderBy(c => c).ToList();

    /// Distinct PII classes covered by a set of catalog paths — for
    /// patch/correct events where the delta is what matters.
    public static IReadOnlyList<string> ForPaths(IEnumerable<string> paths)
        => paths
            .Select(p => Catalog.SpecFor(p)?.PiiClass?.ToString())
            .Where(c => c is not null).Cast<string>()
            .Distinct(StringComparer.Ordinal).OrderBy(c => c).ToList();

    /// The class of a single searchable field (blind-index searches).
    public static IReadOnlyList<string> ForPath(string path)
        => ForPaths([path]);
}
