using System.Text.Json;
using Lola.Application.Admin;
using Lola.Application.Audit;
using Lola.Application.Events;

namespace Lola.Application.Vault;

/// Persist one classification result onto the vault doc row (M18 P2)
/// — the scan drain invokes this after a Clean verdict and before
/// promote. The whole DocClassification is PII-class in the aggregate
/// (extracted fields) so it lands `vault:`-encrypted via the repo;
/// the event + audit write carry kind/confidence/finding NAMES only
/// (invariant 3). P3 extends this handler with finding→condition
/// mapping — the domain mapping point is here.
public sealed record RecordDocClassificationCommand(
    string TenantId, string VaultDocId, byte[] Head);

public sealed record DocClassifiedOutcome(
    string Kind, double Confidence, int FindingCount);

public static class RecordDocClassificationHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<DocClassifiedOutcome> Handle(
        RecordDocClassificationCommand c, IDocClassifier classifier,
        IVaultDocRepository vault, ITenantAuditLog audit,
        ILoanEventLog events, TimeProvider clock,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var doc = await vault.GetAsync(c.VaultDocId, ct)
            ?? throw Errors.DomainException.NotFound(
                $"vault doc {c.VaultDocId}");
        var result = await classifier.ClassifyAsync(
            doc, c.Head, ct);

        var utc = clock.GetUtcNow()
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await vault.SaveClassificationAsync(c.VaultDocId,
            JsonSerializer.Serialize(result, JsonOpts), utc, ct);

        // Metadata only — kind, confidence and finding NAMES; the
        // extracted values never ride the spines (invariant 3).
        var findings = result.Findings.Count == 0
            ? "none"
            : string.Join(",",
                result.Findings.Select(f => f.Kind));
        var actor = "auto:classifier";
        await audit.WriteAsync(actor, "doc_classified", "vault_doc",
            c.VaultDocId, ["vault_docs"],
            $"kind={result.DocKind} confidence={result.Confidence:0.00} findings={findings}",
            ct);
        await events.WriteAsync(new LoanEventDraft(
            doc.ApplicationId, LoanEventKind.DocClassified, actor,
            "vault_doc", c.VaultDocId,
            FieldPath: "vault_docs.classification",
            AfterVal: result.DocKind), ct);
        return new DocClassifiedOutcome(result.DocKind,
            result.Confidence, result.Findings.Count);
    }
}
