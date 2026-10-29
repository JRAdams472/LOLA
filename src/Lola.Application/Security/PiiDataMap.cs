using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lola.Domain.Applications;
using Lola.Domain.Classification;

namespace Lola.Application.Security;

/// Generated PII data-map (MPE-118, ADR-0008) — the canonical answer to
/// "where can personal data live". Two sources:
///   1. the field catalog — every spec carrying a `piiClass`, with its
///      index mode and derived storage location(s): the JSON1 payload
///      (always, as ciphertext), a party index column (the eight
///      `*_ct` columns on application_parties), or a blind-index row;
///   2. `[PiiClass]`-attributed members — control-plane DTO surfaces
///      (AdminUserDto email/name/mfa-ref).
/// Output is deterministic (sorted, no timestamps) so the committed
/// `docs/pii-data-map.json` can be drift-checked byte-for-byte.
public static class PiiDataMap
{
    // The eight ciphertext index columns on application_parties
    // (0002_applications.sql) — leaf names under `party[n].*`.
    private static readonly HashSet<string> PartyIndexColumns =
        new(StringComparer.Ordinal)
        {
            "first_name", "middle_name", "last_name", "suffix",
            "ssn", "dob", "email", "phone"
        };

    public static JsonObject Generate(params Assembly[] memberAssemblies)
    {
        var fields = new JsonArray();
        foreach (var spec in FieldCatalog.Current.PiiFields
                     .OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var storage = new List<string> { "payload" };
            if (spec.Path.StartsWith("party[", StringComparison.Ordinal) &&
                PartyIndexColumns.Contains(LeafName(spec.Path)))
                storage.Add("index-column");
            if (spec.Index == "blind") storage.Add("blind-index");
            fields.Add(new JsonObject
            {
                ["path"] = spec.Path,
                ["piiClass"] = spec.PiiClass!.Value.ToString(),
                ["index"] = spec.Index,
                ["storage"] = new JsonArray(
                    storage.Select(s => (JsonNode)s).ToArray())
            });
        }

        var members = new JsonArray();
        foreach (var (type, member, cls) in memberAssemblies
                     .SelectMany(AttributedMembers)
                     .OrderBy(m => m.Type.FullName, StringComparer.Ordinal)
                     .ThenBy(m => m.Member, StringComparer.Ordinal))
        {
            members.Add(new JsonObject
            {
                ["type"] = type.FullName,
                ["member"] = member,
                ["piiClass"] = cls,
                ["location"] = type.Namespace?.Contains(".Admin") == true ||
                               type.Namespace?.Contains(".Identity") == true
                    ? "control-plane" : "tenant-payload"
            });
        }

        return new JsonObject
        {
            ["schema"] = "lola/pii-data-map/v1",
            ["fields"] = fields,
            ["members"] = members
        };
    }

    public static string GenerateJson(params Assembly[] memberAssemblies) =>
        Generate(memberAssemblies).ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static IEnumerable<(Type Type, string Member, string Class)>
        AttributedMembers(Assembly asm)
    {
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e)
        { types = [.. e.Types.Where(t => t is not null)!]; }
        foreach (var t in types)
        {
            if (t.FullName?.Contains("<") == true) continue;
            foreach (var p in t.GetProperties(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var attr = p.GetCustomAttribute<PiiClassAttribute>();
                if (attr is not null)
                    yield return (t, p.Name, attr.Class.ToString());
            }
        }
    }

    private static string LeafName(string path) =>
        path[(path.LastIndexOf('.') + 1)..];
}
