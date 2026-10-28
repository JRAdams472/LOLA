using System.Text.Json.Nodes;

namespace Lola.Domain.Applications;

/// The canonical application document (ADR-0006): a JSON object
/// addressed by catalog `path` segments — `party[0].ssn` walks
/// root.party[0].ssn. PII values are ciphertext inside the stored
/// payload; the app layer decrypts via the repo. Path addressing keeps
/// every layer (validator, mappers, repo) on the same coordinate system.
public sealed class ApplicationDocument
{
    private readonly JsonObject _root;

    private ApplicationDocument(JsonObject root) => _root = root;

    public static ApplicationDocument Empty() => new([]);

    public static ApplicationDocument Parse(string json)
    {
        var node = JsonNode.Parse(json)
            ?? throw new ArgumentException("payload is not valid JSON");
        return new ApplicationDocument(node.AsObject());
    }

    public static ApplicationDocument FromObject(JsonObject root) => new(root);

    public string ToJson() => _root.ToJsonString();

    public JsonObject CloneRoot() =>
        (JsonObject)JsonNode.Parse(_root.ToJsonString())!;

    public JsonNode? Get(string path) => Resolve(path).Node;

    /// Sets a value at a catalog path, creating intermediate objects
    /// and arrays as needed (`party[0].ssn` materializes `party: []`).
    public void Set(string path, JsonNode? value)
    {
        var segments = ParsePath(path);
        JsonNode current = _root;
        for (var i = 0; i < segments.Count - 1; i++)
            current = Child(current, segments[i], segments[i + 1], create: true)!;
        var last = segments[^1];
        if (last.Index is { } idx)
        {
            var arr = current.AsArray();
            while (arr.Count <= idx) arr.Add(null);
            arr[idx] = value;
        }
        else current.AsObject()[last.Name!] = value;
    }

    /// Enumerates every leaf path currently present, in catalog order —
    /// how writers derive index columns without knowing the shape.
    public IEnumerable<(string Path, JsonNode Node)> Leaves()
    {
        foreach (var pair in Walk(_root, ""))
            yield return pair;
    }

    private static IEnumerable<(string, JsonNode)> Walk(JsonNode node, string prefix)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj)
                    foreach (var leaf in Walk(kv.Value!, Join(prefix, kv.Key)))
                        yield return leaf;
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                    foreach (var leaf in Walk(arr[i]!, $"{prefix}[{i}]"))
                        yield return leaf;
                break;
            default:
                if (node is not null) yield return (prefix, node);
                break;
        }
    }

    private (JsonNode? Node, JsonNode? Parent) Resolve(string path)
    {
        var segments = ParsePath(path);
        JsonNode current = _root;
        foreach (var seg in segments)
        {
            current = Child(current, seg, None, create: false)!;
            if (current is null) return (null, null);
        }
        return (current, null);
    }

    private static JsonNode? Child(JsonNode node, Segment seg, Segment next, bool create)
    {
        if (seg.Index is { } idx)
        {
            if (node is not JsonArray arr) return null;
            while (create && arr.Count <= idx) arr.Add(null);
            if (idx >= arr.Count) return null;
            var child = arr[idx];
            if (child is null && create)
            {
                child = next.Index is not null ? new JsonArray() : new JsonObject();
                arr[idx] = child;
            }
            return child;
        }
        var obj = node.AsObject();
        if (!obj.TryGetPropertyValue(seg.Name!, out var child2) || child2 is null)
        {
            if (!create) return null;
            child2 = next.Index is not null ? new JsonArray() : new JsonObject();
            obj[seg.Name!] = child2;
        }
        return child2;
    }

    private sealed record Segment(string? Name, int? Index);

    private static readonly Segment None = new(null, null);

    private static List<Segment> ParsePath(string path)
    {
        var segments = new List<Segment>();
        var i = 0;
        var name = "";
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                if (name.Length > 0) { segments.Add(new(name, null)); name = ""; }
                i++;
            }
            else if (c == '[')
            {
                if (name.Length > 0) { segments.Add(new(name, null)); name = ""; }
                var end = path.IndexOf(']', i);
                segments.Add(new(null, int.Parse(path[(i + 1)..end])));
                i = end + 1;
            }
            else { name += c; i++; }
        }
        if (name.Length > 0) segments.Add(new(name, null));
        return segments;
    }

    private static string Join(string prefix, string name) =>
        prefix.Length == 0 ? name : $"{prefix}.{name}";
}
