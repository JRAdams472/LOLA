using System.Text.Json.Nodes;
using Lola.Application.Identity;
using Lola.Domain.Applications;

namespace Lola.Application.Applications;

/// Masked-by-default for document payloads (invariant 8): the JSON1 doc
/// bypasses [PiiClass]-attribute masking, so the catalog does the job —
/// every leaf whose spec carries a piiClass is replaced by "•••"+last4
/// unless the caller's role→visibility grants that class plaintext
/// (VisibilityContext, set by the masking middleware — same source the
/// attribute pipeline reads, so JSON and DTO masking can't disagree).
public static class DocMasker
{
    private static readonly FieldCatalog Catalog = FieldCatalog.Current;

    public static void MaskInPlace(ApplicationDocument doc)
    {
        var visible = VisibilityContext.Current;
        foreach (var (path, node) in doc.Leaves())
        {
            var spec = Catalog.SpecFor(path);
            if (spec?.PiiClass is not { } cls || visible.Contains(cls)) continue;
            var text = node is JsonValue v && v.TryGetValue<string>(out var s)
                ? s : node.ToJsonString();
            doc.Set(path, Mask(text));
        }
    }

    /// Same shape as PiiMasker.MaskValue — "•••" + last-4.
    private static string Mask(string value) =>
        value.Length <= 4 ? "•••" : $"•••{value[^4..]}";
}
