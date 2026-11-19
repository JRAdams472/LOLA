using System.Text.Json.Nodes;
using Lola.Application.Security;
using Lola.Domain.Applications;

namespace Lola.Application.Events;

/// Mutation-diff writer for `loan_events` (M15 P2): computes per-path
/// before/after against the PRE-merge document — the repo hands it the
/// plaintext doc it revealed inside its own write transaction, so before
/// values can't be raced by a concurrent patch — and encodes PII-classed
/// values via the tenant blind index so the log never rests plaintext
/// (invariant 1). The caller — the repository that owns BEGIN IMMEDIATE —
/// writes the returned drafts on its own connection, so a mutation
/// rollback takes the event rows with it. No-op patches emit nothing.
public static class LoanEventWriter
{
    private static readonly FieldCatalog Catalog = FieldCatalog.Current;

    public static async Task<IReadOnlyList<LoanEventDraft>> FieldChangesAsync(
        string appId, string actor, ApplicationDocument before,
        IReadOnlyDictionary<string, string> patch, IBlindIndex blind,
        CancellationToken ct = default)
    {
        var drafts = new List<LoanEventDraft>(patch.Count);
        foreach (var (path, value) in patch)
        {
            var beforeVal = ToValueString(before.Get(path));
            if (beforeVal == value)
                continue;
            var draft = new LoanEventDraft(appId, LoanEventKind.FieldChange,
                actor, "application", appId, path,
                Catalog.SpecFor(path)?.PiiClass, beforeVal, value);
            drafts.Add(await LoanEventEncoding.EncodeAsync(draft, blind, ct));
        }
        return drafts;
    }

    private static string? ToValueString(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v => v.ToString(),
        _ => node.ToJsonString()
    };
}
