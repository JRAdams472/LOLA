using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lola.Domain.Applications;

namespace Lola.Application.Applications;

/// Catalog-driven validation (MPE-110): every check derives from
/// `mismo-field-map.json` — required-tier presence, type, pattern, and
/// enum — returning structured FieldError[] operators can correct
/// against. Repeating containers (party[i] past [0]) validate
/// type/pattern/enum when present but aren't required — the catalog
/// templates them once.
public static class FieldValidator
{
    private static readonly FieldCatalog Catalog = FieldCatalog.Current;

    public static IReadOnlyList<FieldError> Validate(ApplicationDocument doc)
    {
        var errors = new List<FieldError>();
        var leaves = doc.Leaves().ToDictionary(l => l.Path, l => l.Node);

        foreach (var spec in Catalog.Fields)
        {
            var node = doc.Get(spec.Path);
            if (node is null)
            {
                if (spec.Required)
                    errors.Add(new(spec.Path, "required",
                        $"required field '{spec.Path}' is missing"));
                continue;
            }
            CheckValue(spec, node, errors);
        }

        // Repeating containers past the template index: validate every
        // concrete leaf's type/pattern/enum via the normalized spec.
        foreach (var (path, node) in doc.Leaves())
        {
            if (leaves.ContainsKey(path) && Catalog.Find(path) is not null) continue;
            var spec = Catalog.SpecFor(path);
            if (spec is null) continue;
            CheckValue(spec, node, errors, path);
        }

        return errors;
    }

    private static void CheckValue(FieldSpec spec, JsonNode node,
        List<FieldError> errors, string? concretePath = null)
    {
        var path = concretePath ?? spec.Path;
        var text = node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s : node.ToJsonString();

        if (spec.Enum is { Count: > 0 } allowed && !allowed.Contains(text))
            errors.Add(new(path, "enum",
                $"'{text}' is not one of: {string.Join(", ", allowed)}"));

        if (spec.Pattern is { } pattern && !Regex.IsMatch(text, pattern))
            errors.Add(new(path, "pattern",
                $"'{text}' does not match {pattern}"));

        var ok = spec.Type switch
        {
            "int" => int.TryParse(text, out _),
            "decimal" => decimal.TryParse(text, out _),
            "date" => DateOnly.TryParse(text, out _),
            "bool" => bool.TryParse(text, out _),
            _ => true
        };
        if (!ok)
            errors.Add(new(path, "type", $"'{text}' is not a valid {spec.Type}"));
    }
}
