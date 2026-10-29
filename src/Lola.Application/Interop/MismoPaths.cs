using System.Text.RegularExpressions;
using Lola.Domain.Applications;

namespace Lola.Application.Interop;

/// Index splicing between canonical doc paths and MISMO XML paths.
/// Both address the same repeating containers in the same order —
/// `party[i]`→`PARTY[i]`, `address`→`ADDRESS`, `employment`→
/// `EMPLOYMENT`, `asset`→`ASSET`, `liability`→`LIABILITY` — so
/// concrete indexes transfer positionally; mismo placeholders the
/// canonical model doesn't carry (e.g. `CONTACT[0]`) stay at 0.
internal static partial class MismoPaths
{
    /// Canonical `party[1].address[0].street` + template
    /// `PARTY[0]/ADDRESSES/ADDRESS[0]/AddressLineText` →
    /// `PARTY[1]/ADDRESSES/ADDRESS[0]/AddressLineText`.
    public static string Concrete(string canonicalPath, string mismoTemplate)
    {
        var indexes = IndexToken().Matches(canonicalPath)
            .Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        var taken = 0;
        return IndexToken().Replace(mismoTemplate, m =>
            taken < indexes.Length ? $"[{indexes[taken++]}]" : m.Value);
    }

    /// Match an XML leaf's typed ancestor path against catalog
    /// templates by segment name; on match returns the spec plus the
    /// concrete canonical path with the XML indexes spliced back —
    /// taken only from segments the template indexes, in order
    /// (flattened MISMO containers' indexes are dropped).
    public static (FieldSpec Spec, string CanonicalPath)? TryImport(
        IReadOnlyList<(string Name, int Index)> xmlPath,
        FieldCatalog catalog)
    {
        foreach (var spec in catalog.Fields)
        {
            if (spec.MismoPath is not { } template) continue;
            var tSegs = template.Split('/');
            if (tSegs.Length != xmlPath.Count) continue;

            var xmlIndexes = new List<int>();
            var matches = true;
            for (var j = 0; j < tSegs.Length; j++)
            {
                if (tSegs[j].Split('[')[0] != xmlPath[j].Name)
                { matches = false; break; }
                if (tSegs[j].Contains('[')) xmlIndexes.Add(xmlPath[j].Index);
            }
            if (!matches) continue;

            var taken = 0;
            var canonical = IndexToken().Replace(spec.Path, m =>
                taken < xmlIndexes.Count ? $"[{xmlIndexes[taken++]}]" : m.Value);
            return (spec, canonical);
        }
        return null;
    }

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex IndexToken();
}
