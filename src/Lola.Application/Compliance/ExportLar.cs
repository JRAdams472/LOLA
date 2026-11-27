using System.Text;
using Lola.Application.Admin;
using Lola.Application.Events;
using Lola.Application.Applications;
using Lola.Application.Identity;
using Lola.Domain.Hmda;

namespace Lola.Application.Compliance;

/// GET …/hmda/export?year= (M24 P2) — the reporting-year pipe-delimited
/// LAR artifact per the FIG: one transmittal-sheet row + one LAR row
/// per application (latest assembly per app), columns in canonical
/// FIG order. Generated inside the tenant boundary; every export
/// writes hash-chained `hmda_export` loan events attributing actor +
/// year + row count (invariant 2). Zero rows → 409, never an empty
/// silent artifact.
public sealed record ExportLarCommand(
    string TenantId, int Year, string ActorId, ActorScope Scope);

public static class ExportLarHandler
{
    public static async Task<HmdaExportResult> Handle(
        ExportLarCommand c, IHmdaRepository hmda, ILoanEventLog events,
        HmdaOptions options, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "LAR export is a staff action");
        if (c.Year is < 2000 or > 2100)
            throw Errors.DomainException.BadRequest(
                "year must be a plausible reporting year");

        var rows = await hmda.ListLarRowsByYearAsync(c.Year, ct);
        // Latest assembly per application — the export never double-
        // counts a re-assembled loan.
        var latest = rows
            .GroupBy(r => r.ApplicationId)
            .Select(g => g.First())
            .OrderBy(r => r.Uli, StringComparer.Ordinal)
            .ToList();
        if (latest.Count == 0)
            throw Errors.DomainException.Conflict(
                $"no assembled LAR rows for reporting year {c.Year}");

        var sb = new StringBuilder();
        sb.AppendLine(TransmittalRow(c.Year, options.Lei, latest.Count));
        foreach (var row in latest)
        {
            var fields = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, string>>(row.RowJson)
                ?? new Dictionary<string, string>();
            sb.AppendLine(string.Join("|",
                LarSchema.ExportColumns.Select(col =>
                    fields.TryGetValue(col, out var v) ? v : "")));
        }

        foreach (var row in latest)
        {
            await events.WriteAsync(new LoanEventDraft(row.ApplicationId,
                LoanEventKind.HmdaExport, c.ActorId,
                "hmda_lar", row.LarRowId, "compliance", null, null,
                $"year:{c.Year};rows:{latest.Count}"), ct);
        }

        return new HmdaExportResult(c.Year, latest.Count,
            Encoding.UTF8.GetBytes(sb.ToString()),
            $"hmda-lar-{c.Year}.txt");
    }

    /// Transmittal-sheet row (record identifier 1) — institution
    /// identity + filing metadata. Institution contact fields are not
    /// yet modeled tenant configuration; they emit NA per the FIG's
    /// explicit-absence convention.
    private static string TransmittalRow(int year, string lei, int rowCount) =>
        string.Join("|",
            "1",                 // record identifier
            lei,                 // LEI
            year.ToString(),     // calendar year
            "4",                 // calendar quarter (annual filer = 4)
            "NA",                // contact name — not yet tenant config
            "NA",                // contact phone
            "NA",                // contact email
            "NA",                // institution street address
            "NA",                // city
            "NA",                // state
            "NA",                // zip
            "LOLA",              // institution name (demo posture)
            rowCount.ToString(), // lar row count (informational)
            "1");                // tax-id placeholder scope — NA-grade
}
