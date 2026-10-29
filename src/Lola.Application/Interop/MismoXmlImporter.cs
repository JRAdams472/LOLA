using System.Xml;
using System.Xml.Linq;
using Lola.Domain.Applications;

namespace Lola.Application.Interop;

/// MISMO XML → draft payload (hand-rolled, catalog-driven). Every leaf
/// element's ancestor path is matched against catalog `mismoPath`
/// templates by segment name; matched values land on the concrete
/// canonical path with XML indexes spliced back. Elements that match
/// nothing are skipped — forward/backward-version tolerance by design.
/// XXE-safe: DTDs prohibited, no resolver, 2 MB cap.
public static class MismoXmlImporter
{
    private const int MaxXmlChars = 2_000_000;

    public sealed record Result(ApplicationDocument Doc, int ImportedFields);

    public static Result Import(string xml)
    {
        if (xml.Length > MaxXmlChars)
            throw new ArgumentException(
                $"MISMO document exceeds the {MaxXmlChars:N0}-character limit", nameof(xml));

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(
            new StringReader(xml), settings);
        var doc = XDocument.Load(reader);

        var loanApp = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "LOAN_APPLICATION")
            ?? throw new ArgumentException(
                "no LOAN_APPLICATION element found", nameof(xml));

        var catalog = FieldCatalog.Current;
        var appDoc = ApplicationDocument.Empty();
        var imported = 0;

        foreach (var leaf in loanApp.Descendants()
                     .Where(e => !e.HasElements))
        {
            var path = AncestorPath(leaf, loanApp);
            if (path is null) continue;
            if (MismoPaths.TryImport(path, catalog) is not { } hit) continue;
            appDoc.Set(hit.CanonicalPath, leaf.Value);
            imported++;
        }
        return new Result(appDoc, imported);
    }

    /// `leaf`'s typed ancestor path from `root` inclusive — each
    /// segment carries its position among same-named siblings
    /// (always explicit, so first siblings carry index 0).
    private static List<(string Name, int Index)>? AncestorPath(
        XElement leaf, XElement root)
    {
        var segments = new List<(string, int)>();
        for (var e = leaf; e is not null; e = e.Parent)
        {
            segments.Add((e.Name.LocalName,
                e.ElementsBeforeSelf(e.Name).Count()));
            if (e == root) { segments.Reverse(); return segments; }
        }
        return null;
    }
}
