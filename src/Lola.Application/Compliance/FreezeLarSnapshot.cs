using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Identity;

namespace Lola.Application.Compliance;

/// POST …/hmda/freeze (M24 P2) — reporting-year snapshot freeze:
/// appends ONE `lar_snapshots` row + the row mappings covering every
/// assembled row for the year. Because the tables are append-only, a
/// re-freeze creates a NEW snapshot — the original freeze never
/// changes shape, which is exactly the immutability the acceptance
/// requires. Zero rows → 409.
public sealed record FreezeLarSnapshotCommand(
    string TenantId, int Year, string ActorId, ActorScope Scope);

public static class FreezeLarSnapshotHandler
{
    public static async Task<LarSnapshotRecord> Handle(
        FreezeLarSnapshotCommand c, IHmdaRepository hmda,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "LAR snapshot freeze is a staff action");
        if (c.Year is < 2000 or > 2100)
            throw Errors.DomainException.BadRequest(
                "year must be a plausible reporting year");

        var rows = await hmda.ListLarRowsByYearAsync(c.Year, ct);
        if (rows.Count == 0)
            throw Errors.DomainException.Conflict(
                $"no assembled LAR rows for reporting year {c.Year}");

        return await hmda.AddSnapshotAsync(c.Year,
            rows.Select(r => r.LarRowId).ToList(), c.ActorId, ct);
    }
}

/// HMDA institution configuration (M24 P2) — the LEI/NMLS id are
/// institution-level inputs, env-configured (`HMDA__LEI`,
/// `HMDA__NMLSID`). Defaults are the CFPB's own published example
/// values for the demo posture; production tenants set their real
/// LEI per institution.
public sealed record HmdaOptions
{
    public string Lei { get; init; } = "10BX939C5543TQA1144M";
    public string? NmlsId { get; init; }
}
