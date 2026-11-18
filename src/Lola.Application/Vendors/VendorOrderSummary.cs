using System.Text.Json.Nodes;

namespace Lola.Application.Vendors;

/// Normalized one-line summaries for the officer Steps surface
/// (MPE-229) — the report's decision-grade fields rendered as text.
/// The report envelope carries no PII-tagged members; the summary
/// picks only the outcome fields officers scan for.
public static class VendorOrderSummary
{
    /// `report` is the `report` member of the stored result envelope
    /// ({outcome, failure_reason, report}). Null report → status
    /// vocabulary only.
    public static string? Summarize(VendorKind kind, JsonObject? report)
    {
        if (report is null) return null;
        string? Str(string key) =>
            report[key]?.GetValue<string>() is { Length: > 0 } v
                ? v : null;
        long? Num(string key)
        {
            try { return report[key]?.GetValue<long>(); }
            catch { }
            try { return report[key]?.GetValue<int>(); }
            catch { }
            try
            {
                return report[key]?.GetValue<decimal>() is { } d
                    ? (long)d : null;
            }
            catch { return null; }
        }
        bool? Bool(string key)
        {
            try { return report[key]?.GetValue<bool>(); }
            catch { return null; }
        }
        return kind switch
        {
            VendorKind.Credit =>
                $"score {Num("decisionScore")?.ToString() ?? "—"} · " +
                $"{(report["bureaus"] is JsonArray b ? b.Count : 0)} bureaus",
            VendorKind.Voe =>
                $"employment {Str("employmentStatus") ?? "—"} · " +
                $"{Num("tenureMonths")?.ToString() ?? "—"}mo tenure",
            VendorKind.Voi =>
                $"{(Num("annualizedBaseIncome") is { } i
                    ? $"${i:N0}/yr" : "income")} " +
                (Bool("incomeVerified") == true ? "verified" : "unverified"),
            VendorKind.Flood =>
                $"zone {Str("floodZone") ?? "—"} · " +
                (Bool("insuranceRequired") == true
                    ? "insurance required" : "no insurance required"),
            VendorKind.Fraud =>
                $"{(Bool("ofacMatch") == true ? "OFAC match"
                    : "OFAC clear")} · " +
                $"identity {Num("identityScore")?.ToString() ?? "—"}",
            VendorKind.Aus =>
                $"{Str("recommendation") ?? "—"} · " +
                $"{Str("riskClassification") ?? "—"} risk",
            _ => Str("status")
        };
    }
}
