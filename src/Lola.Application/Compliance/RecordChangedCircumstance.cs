using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Events;
using Lola.Application.Identity;

namespace Lola.Application.Compliance;

/// POST …/applications/{id}/tolerance/resets (M21 P2) — records a
/// changed circumstance under §1026.19(e)(3)(iv): the NEXT disclosure
/// version becomes the new baseline for the listed fees only. The
/// reset is pending (`rebased_version_id` NULL) until a version
/// stamps it — recorded intent first, baseline shift on re-issue.
public sealed record RecordChangedCircumstanceCommand(
    string TenantId, string AppId, string ActorId, ActorScope Scope,
    string ReasonCode, IReadOnlyList<string> FeeCodes);

public static class RecordChangedCircumstanceHandler
{
    public static async Task<ToleranceResetRecord> Handle(
        RecordChangedCircumstanceCommand c, IApplicationRepository apps,
        IToleranceRepository tolerance, ILoanEventLog events,
        CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope != ActorScope.Staff)
            throw Errors.DomainException.Forbidden(
                "changed circumstances are a staff action");
        await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, ActorScope.Staff, ct);

        if (!ToleranceResetReasons.All.Contains(c.ReasonCode))
            throw Errors.DomainException.BadRequest(
                $"invalid reason code '{c.ReasonCode}'");
        var codes = c.FeeCodes
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (codes.Count == 0)
            throw Errors.DomainException.BadRequest(
                "affected_fee_codes must be non-empty");

        var hasBaseline = (await tolerance.ListVersionsAsync(
                c.AppId, "le", ct)).Count > 0
            || (await tolerance.ListVersionsAsync(
                c.AppId, "cd", ct)).Count > 0;
        if (!hasBaseline)
            throw Errors.DomainException.Conflict(
                "no disclosure version exists to rebase");

        var reset = await tolerance.AddResetAsync(c.AppId,
            c.ReasonCode,
            System.Text.Json.JsonSerializer.Serialize(
                codes.OrderBy(x => x)),
            c.ActorId, ct);
        await events.WriteAsync(new LoanEventDraft(c.AppId,
            LoanEventKind.ToleranceReset, c.ActorId,
            "tolerance_reset", reset.ResetId, "tolerance", null,
            null,
            $"{c.ReasonCode}: {string.Join(",",
                codes.OrderBy(x => x))}"), ct);
        return reset;
    }
}
