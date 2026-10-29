using System.Text.Json.Nodes;
using System.Xml.Linq;
using Lola.Domain.Applications;

namespace Lola.Application.Interop;

/// Canonical doc → ULDD structural export (catalog `ulddField` =
/// `CONTAINER/Field`). Not a certified ULDD submission — a structural
/// rendering: repeating canonical groups become sibling containers
/// (`BORROWER` per party, `ASSET`/`LIABILITY` per index), party-scoped
/// `ADDRESS`/`EMPLOYER`/`INCOME` nest inside their `BORROWER`, and the
/// skeleton containers are emitted even when empty so consumers can
/// rely on them.
public static class UlddExporter
{
    private static readonly string[] TopLevel =
        ["LOAN", "PROPERTY", "DECLARATION", "HMDA", "ASSET", "LIABILITY"];

    public static string Export(ApplicationDocument doc)
    {
        var root = new XElement("ULDD");
        var elements = new Dictionary<string, XElement>();
        var catalog = FieldCatalog.Current;

        XElement Top(string name) => Nest(elements, root, $"top:{name}", name);

        foreach (var (path, node) in doc.Leaves())
        {
            var spec = catalog.SpecFor(path);
            if (spec?.UlddField is not { } field) continue;
            var slash = field.IndexOf('/');
            if (slash < 0) continue;
            var container = field[..slash];
            var name = field[(slash + 1)..];

            XElement? parent = container switch
            {
                "BORROWER" => Nest(elements, root,
                    $"borrower:{GroupIndex(path, "party")}", "BORROWER"),
                "ADDRESS" => Nest(elements,
                    Nest(elements, root,
                        $"borrower:{GroupIndex(path, "party")}", "BORROWER"),
                    $"address:{GroupIndex(path, "party")}:{GroupIndex(path, "address")}",
                    "ADDRESS"),
                "EMPLOYER" => Nest(elements,
                    Nest(elements, root,
                        $"borrower:{GroupIndex(path, "party")}", "BORROWER"),
                    $"employer:{GroupIndex(path, "party")}:{GroupIndex(path, "employment")}",
                    "EMPLOYER"),
                "INCOME" => Nest(elements,
                    Nest(elements,
                        Nest(elements, root,
                            $"borrower:{GroupIndex(path, "party")}", "BORROWER"),
                        $"employer:{GroupIndex(path, "party")}:{GroupIndex(path, "employment")}",
                        "EMPLOYER"),
                    $"income:{GroupIndex(path, "party")}:{GroupIndex(path, "employment")}",
                    "INCOME"),
                "ASSET" or "LIABILITY" => Nest(elements, root,
                    $"{container}:{GroupIndex(path, container.ToLowerInvariant())}",
                    container),
                _ => TopLevel.Contains(container) ? Top(container) : null
            };
            parent?.Add(new XElement(name, Text(node)));
        }

        // Skeleton — required containers exist even with no fields.
        foreach (var c in TopLevel) Top(c);

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null), root).ToString();
    }

    /// Get-or-create a named child under `parent`, keyed by `key`.
    private static XElement Nest(Dictionary<string, XElement> elements,
        XElement parent, string key, string name)
    {
        if (!elements.TryGetValue(key, out var el))
            parent.Add(elements[key] = el = new XElement(name));
        return el;
    }

    /// Index of the `name[n]` segment in a canonical path (0 when the
    /// segment is absent — e.g. `demographics.*` has no `party`).
    private static int GroupIndex(string path, string name)
    {
        var token = $"{name}[";
        var at = path.IndexOf(token, StringComparison.Ordinal);
        return at < 0 ? 0 : int.Parse(
            path[(at + token.Length)..path.IndexOf(']', at)]);
    }

    private static string Text(JsonNode node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s : node.ToJsonString();
}
