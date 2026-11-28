using System.Text.Json.Nodes;
using Lola.Application.Events;

namespace Lola.Application.Aus;

/// AUS completion processor (M25 P2) — invoked from the generic
/// vendor-completion flow when a kind=aus order lands. The adapter
/// boundary normalizes vendor XML into the canonical report shape;
/// this processor persists ONE append-only result row + finding rows,
/// then auto-populates conditions for Required/Fatal findings deduped
/// by vendor code (the M23 sweep discipline — a repeat completion
/// never double-opens). Raw response XML stays in the tenant-file
/// table; the loan event already written by the generic flow carries
/// metadata only (invariant 3).
public static class ProcessAusCompletion
{
    public static async Task<string?> Handle(
        string appId, string orderId, string vendorHint,
        JsonObject report, string actor,
        IAusResultsStore store, IConditionRepository conditions,
        CancellationToken ct)
    {
        var recommendation = report["recommendation"]?.GetValue<string>() ?? "unable-to-evaluate";
        var riskClass = report["riskClassification"]?.GetValue<string>();
        var caseId = report["caseId"]?.GetValue<string>()
            ?? report["externalId"]?.GetValue<string>();
        var responseXml = report["responseXml"]?.GetValue<string>();
        var vendor = report["ausVendor"]?.GetValue<string>()
            ?? (report["ausEngine"]?.GetValue<string>() is { } engine
                && engine.Contains("lpa", System.StringComparison.OrdinalIgnoreCase)
                    ? "lpa" : "du");

        var findings = new List<(string code, string category, string severity, string message)>();
        if (report["findings"] is JsonArray arr)
        {
            foreach (var f in arr)
            {
                if (f is not JsonObject fo) continue;
                findings.Add((
                    fo["code"]?.GetValue<string>()
                        ?? fo["vendorCode"]?.GetValue<string>() ?? "UNK",
                    fo["category"]?.GetValue<string>() ?? "other",
                    fo["severity"]?.GetValue<string>() ?? "info",
                    fo["message"]?.GetValue<string>() ?? ""));
            }
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(responseXml ?? recommendation)))
            .ToLowerInvariant();

        var row = await store.AddResultAsync(appId, vendor, orderId,
            caseId, recommendation, riskClass, responseXml, hash,
            findings.Count, actor, ct);

        // Severity-driven condition auto-population, deduped by vendor
        // code: an open aus_finding condition linked to the same code
        // is reused, never double-opened.
        var open = await conditions.ListByApplicationAsync(appId, "open", ct);
        foreach (var f in findings)
        {
            string? conditionId = null;
            if (f.severity is "required" or "fatal")
            {
                var linkedId = $"{vendor}:{f.code}";
                var existing = open.FirstOrDefault(c =>
                    c.Kind == ConditionKind.AusFinding
                    && c.LinkedEntityId == linkedId);
                if (existing is not null)
                {
                    conditionId = existing.ConditionId;
                }
                else
                {
                    var title = $"AUS {vendor}: {f.message}";
                    if (title.Length > 200) title = title[..200];
                    var condition = await conditions.OpenAsync(
                        new ConditionDraft(appId, ConditionKind.AusFinding,
                            title,
                            Detail: $"vendor={vendor};code={f.code};category={f.category};severity={f.severity}",
                            Blocking: f.severity == "fatal",
                            LinkedEntity: "aus_finding",
                            LinkedEntityId: linkedId,
                            OpenedBy: "auto:aus"), ct);
                    conditionId = condition.ConditionId;
                }
            }
            await store.AddFindingAsync(row.ResultId, f.code,
                f.category, f.severity, f.message, conditionId, ct);
        }
        return row.ResultId;
    }
}
