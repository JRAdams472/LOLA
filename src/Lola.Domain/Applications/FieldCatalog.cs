using System.Reflection;
using System.Text.Json;
using Lola.Domain.Classification;

namespace Lola.Domain.Applications;

/// One row of the field catalog — a canonical `path` (party[0].ssn)
/// mapped through interop (MISMO XML, ULDD), protection (piiClass +
/// index), and validation (required/core, pattern, enum, trid).
public sealed record FieldSpec(
    string Path,
    string? MismoPath,
    string? UlddField,
    PiiClass? PiiClass,
    /// "none" | "ciphertext" | "blind" — blind = ciphertext + HMAC index.
    string Index,
    string Type,
    bool Required,
    bool Core,
    bool Trid,
    string? Pattern,
    IReadOnlyList<string>? Enum);

/// The single truth for the 1003 model (M5, ADR-0006): every layer —
/// JSON1 payload, index columns, validator, MISMO/ULDD mappers —
/// derives from `mismo-field-map.json`. Breadth lives in data, not
/// hand-written properties, so spec drift is a file diff, not a refactor.
public sealed class FieldCatalog
{
    private static readonly Lazy<FieldCatalog> Instance = new(Load);

    public static FieldCatalog Current => Instance.Value;

    public IReadOnlyList<FieldSpec> Fields { get; private init; } = [];
    private Dictionary<string, FieldSpec> _byPath = [];

    /// Spec lookup for a CONCRETE doc path — array indices normalize to
    /// `[0]` so `party[2].ssn` resolves the `party[0].ssn` spec (the
    /// catalog templates repeating containers once).
    public FieldSpec? SpecFor(string path) =>
        _byPath.GetValueOrDefault(Normalize(path));

    public FieldSpec? Find(string path) => _byPath.GetValueOrDefault(path);

    private static string Normalize(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, @"\[\d+\]", "[0]");

    /// Fields that carry PII — every value at these paths is ciphertext
    /// at rest (payload AND index columns), never plaintext.
    public IEnumerable<FieldSpec> PiiFields =>
        Fields.Where(f => f.PiiClass is not null);

    public IEnumerable<FieldSpec> BlindFields =>
        Fields.Where(f => f.Index == "blind");

    /// The six TRID trigger paths — clock starts when all are present.
    public IEnumerable<FieldSpec> TridFields =>
        Fields.Where(f => f.Trid);

    public IEnumerable<FieldSpec> RequiredFields =>
        Fields.Where(f => f.Required);

    private static FieldCatalog Load()
    {
        var stream = typeof(FieldCatalog).Assembly
            .GetManifestResourceStream("Lola.Domain.Applications.mismo-field-map.json")
            ?? throw new InvalidOperationException("mismo-field-map.json is not embedded");
        using var doc = JsonDocument.Parse(stream);
        var fields = new List<FieldSpec>();
        foreach (var f in doc.RootElement.GetProperty("fields").EnumerateArray())
        {
            fields.Add(new FieldSpec(
                f.GetProperty("path").GetString()!,
                f.TryGetProperty("mismoPath", out var mp) ? mp.GetString() : null,
                f.TryGetProperty("ulddField", out var uf) ? uf.GetString() : null,
                f.TryGetProperty("piiClass", out var pc)
                    ? Enum.Parse<PiiClass>(pc.GetString()!, ignoreCase: true)
                    : null,
                f.TryGetProperty("index", out var ix) ? ix.GetString()! : "none",
                f.TryGetProperty("type", out var ty) ? ty.GetString()! : "string",
                f.TryGetProperty("required", out var rq) && rq.GetBoolean(),
                f.TryGetProperty("core", out var co) && co.GetBoolean(),
                f.TryGetProperty("trid", out var tr) && tr.GetBoolean(),
                f.TryGetProperty("pattern", out var pt) ? pt.GetString() : null,
                f.TryGetProperty("enum", out var en)
                    ? en.EnumerateArray().Select(e => e.GetString()!).ToList()
                    : null));
        }
        var catalog = new FieldCatalog { Fields = fields };
        catalog._byPath = fields.ToDictionary(f => f.Path);
        return catalog;
    }
}
