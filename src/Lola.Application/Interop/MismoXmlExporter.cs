using System.Text.Json.Nodes;
using System.Xml.Linq;
using Lola.Domain.Applications;

namespace Lola.Application.Interop;

/// Canonical doc → MISMO v3.4 message (hand-rolled, catalog-driven —
/// no licensed translator per the megaplan). Every doc leaf whose
/// spec carries a `mismoPath` becomes the tail element of that path;
/// ancestor elements are materialized on demand keyed by their full
/// concrete path so repeating containers (`PARTY[1]`) stay distinct.
public static class MismoXmlExporter
{
    private static readonly XNamespace Ns =
        "http://www.mismo.org/residential/2009/schemas";

    public static string Export(ApplicationDocument doc)
    {
        var catalog = FieldCatalog.Current;
        var root = new XElement(Ns + "MESSAGE",
            new XAttribute("MISMOReferenceModelID", "3.4.0"));
        var deal = new XElement(Ns + "DEAL_SETS",
            new XElement(Ns + "DEAL_SET",
                new XElement(Ns + "DEALS",
                    new XElement(Ns + "DEAL"))));
        root.Add(deal);
        var dealEl = deal.Descendants(Ns + "DEAL").First();
        var byPath = new Dictionary<string, XElement>();

        foreach (var (path, node) in doc.Leaves())
        {
            var spec = catalog.SpecFor(path);
            if (spec?.MismoPath is not { } template) continue;

            var concrete = MismoPaths.Concrete(path, template);
            XElement? parent = dealEl;
            var prefix = "";
            var segments = concrete.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                prefix = prefix.Length == 0 ? segments[i] : $"{prefix}/{segments[i]}";
                var name = segments[i].Split('[')[0];
                if (byPath.TryGetValue(prefix, out var existing))
                {
                    parent = existing;
                    continue;
                }
                var el = new XElement(Ns + name);
                parent!.Add(el);
                byPath[prefix] = el;
                parent = el;
            }
            parent!.Value = Text(node);
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null), root).ToString();
    }

    private static string Text(JsonNode node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s : node.ToJsonString();
}
