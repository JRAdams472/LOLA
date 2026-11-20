using System.Text.Json;

namespace Lola.Application.Vault;

/// Names-only projection of a persisted `DocClassification` — parses
/// the decrypted `classification_json` and yields exactly what staff
/// may see: detected kind, confidence, finding names, field names.
/// The `fields` VALUES are never touched — the read stops at keys
/// (invariants 3, 8). Malformed/legacy payloads degrade to null
/// rather than a 500 on a read path.
internal static class DocIntel
{
    public static DocIntelDto? Of(string? json, string? classifiedUtc)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var kind = root.TryGetProperty("docKind", out var k)
                ? k.GetString() ?? "" : "";
            var confidence = root.TryGetProperty("confidence", out var c)
                && c.ValueKind == JsonValueKind.Number
                    ? c.GetDouble() : 0d;

            var fields = root.TryGetProperty("fields", out var f)
                && f.ValueKind == JsonValueKind.Object
                    ? (IReadOnlyList<string>)f.EnumerateObject()
                        .Select(p => p.Name).ToList()
                    : [];

            var findings = root.TryGetProperty("findings", out var fs)
                && fs.ValueKind == JsonValueKind.Array
                    ? (IReadOnlyList<string>)fs.EnumerateArray()
                        .Select(e => e.TryGetProperty("kind", out var fk)
                            ? fk.GetString() ?? "" : "")
                        .Where(s => s.Length > 0).ToList()
                    : [];

            return new DocIntelDto(
                kind, confidence, findings, fields, classifiedUtc);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
