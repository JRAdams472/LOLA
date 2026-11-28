using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Application.Identity;
using Lola.Domain.Hmda;

namespace Lola.Application.Compliance;

/// POST …/applications/{id}/hmda/assemble (M24 P2) — assembles the
/// LAR record from the application doc + latest ATR evaluation and
/// records it as ONE append-only `lar_rows` row plus the edit-check
/// battery results plus ONE hash-chained `hmda_lar` loan event
/// (counts only — never field values, invariant 3). Re-assembly is a
/// new row, never an update — the lineage is the audit trail.
public sealed record AssembleLarCommand(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class AssembleLarHandler
{
    public static async Task<HmdaLarRowDto> Handle(
        AssembleLarCommand c, IApplicationRepository apps,
        IAtrQmRepository atr, IHmdaRepository hmda, ILoanEventLog events,
        HmdaOptions options, Aus.IAusResultsStore ausStore,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "LAR assembly is a staff action");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, ActorScope.Staff, ct);

        var atrRows = await atr.ListResultsAsync(c.AppId, ct);
        var latestAtr = atrRows.Count > 0 ? atrRows[0] : null;
        // M25 linkage: latest AUS result populates the LAR AUS
        // columns (FIG system/result codes); absent results keep NA.
        var ausRows = await ausStore.ListResultsAsync(c.AppId, ct);
        var latestAus = ausRows.Count > 0
            ? ausRows.OrderByDescending(r => r.CreatedUtc).First() : null;
        var input = HmdaEvaluator.Map(app.Doc, options.Lei, c.AppId,
            latestAtr, options.NmlsId, latestAus?.Vendor,
            latestAus?.Recommendation);
        var fields = LarAssembler.Assemble(input);
        var edits = EditChecks.Run(fields);

        var reportingYear = ReportingYear(fields);
        var rowJson = System.Text.Json.JsonSerializer.Serialize(fields);
        var derivedJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            atr_result_id = latestAtr?.ResultId,
            uli_suffix = input.UliSuffix,
        });

        var record = await hmda.AddLarRowAsync(c.AppId, reportingYear,
            fields["uli"], rowJson, derivedJson, c.ActorId, ct);

        foreach (var e in edits)
        {
            await hmda.AddEditRunAsync(record.LarRowId, e.Tier, e.EditId,
                e.Result, e.Detail, ct);
        }

        var fails = edits.Count(e => e.Result == "fail");
        var warns = edits.Count(e => e.Result == "warn");
        await events.WriteAsync(new LoanEventDraft(c.AppId,
            LoanEventKind.HmdaLar, c.ActorId,
            "hmda_lar", record.LarRowId, "compliance", null, null,
            $"year:{reportingYear};edits:{edits.Count};fail:{fails};warn:{warns}"), ct);

        return GetApplicationHmdaHandler.ToDto(record, edits);
    }

    /// Reporting year = action-taken year when available, else the
    /// current calendar year — the FIG files by action year.
    internal static int ReportingYear(
        IReadOnlyDictionary<string, string> fields)
    {
        if (fields.TryGetValue("action_taken_date", out var d)
            && d.Length == 8 && int.TryParse(d[..4], out var y))
            return y;
        return DateTime.UtcNow.Year;
    }
}
