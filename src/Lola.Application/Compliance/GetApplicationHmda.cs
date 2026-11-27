using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Identity;
using Lola.Domain.Hmda;

namespace Lola.Application.Compliance;

/// GET …/applications/{id}/hmda (M24 P2) — the application's HMDA
/// posture: latest assembled LAR row with its edit-check results
/// grouped by tier, the append-only assembly lineage, and headline
/// figures. Staff-only.
public sealed record GetApplicationHmdaQuery(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class GetApplicationHmdaHandler
{
    public static async Task<HmdaReportDto> Handle(
        GetApplicationHmdaQuery c, IApplicationRepository apps,
        IHmdaRepository hmda, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "HMDA posture is staff-only");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, ActorScope.Staff, ct);

        var rows = await hmda.ListLarRowsAsync(c.AppId, ct);
        if (rows.Count == 0)
            return new HmdaReportDto(null, [],
                new HmdaFiguresDto(null, null, null, null, 0, 0));

        var lineage = new List<HmdaLarRowDto>(rows.Count);
        foreach (var row in rows)
        {
            var runs = await hmda.ListEditRunsAsync(row.LarRowId, ct);
            lineage.Add(ToDto(row, runs.Select(r =>
                new EditResult(r.EditId, r.Tier, r.Result, r.Detail))
                .ToList()));
        }

        var latest = lineage[0];
        return new HmdaReportDto(latest, lineage, new HmdaFiguresDto(
            Val(latest, "action_taken"), Val(latest, "action_taken_date"),
            Val(latest, "rate_spread"), Val(latest, "hoepa_status"),
            latest.Edits.Count(e => e.Result == "fail"),
            latest.Edits.Count(e => e.Result == "warn")));
    }

    internal static HmdaLarRowDto ToDto(LarRowRecord r,
        IReadOnlyList<EditResult> edits)
    {
        IReadOnlyDictionary<string, string> fields;
        try
        {
            fields = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, string>>(r.RowJson)
                ?? new Dictionary<string, string>();
        }
        catch (System.Text.Json.JsonException)
        {
            fields = new Dictionary<string, string>();
        }
        return new HmdaLarRowDto(r.LarRowId, r.Uli, r.ReportingYear,
            fields, edits.Select(e =>
                new HmdaEditDto(e.EditId, e.Tier, e.Result, e.Detail))
                .ToList(), r.Actor, r.CreatedUtc);
    }

    private static string? Val(HmdaLarRowDto row, string key) =>
        row.Fields.TryGetValue(key, out var v) && v != "NA" ? v : null;
}
