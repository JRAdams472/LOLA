using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;

namespace Lola.Application.Pricing;

/// POST …/applications/{id}/unlock (MPE-131) — releases the active
/// lock with a required audited reason; the freeze clears and the row
/// stays in history (append-only, invariant 6).
public sealed record UnlockRateCommand(
    string TenantId, string AppId, string ActorId,
    bool ActorIsBorrower, string Reason);

public static class UnlockRateHandler
{
    public static async Task<bool> Handle(
        UnlockRateCommand c, IApplicationRepository apps,
        IRateLockRepository locks, ITenantAuditLog audit,
        TimeProvider clock, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw DomainException.Forbidden("rate unlock is a staff action");
        if (string.IsNullOrWhiteSpace(c.Reason))
            throw DomainException.BadRequest("an unlock reason is required");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, isBorrower: false, ct);

        var active = await locks.GetActiveAsync(c.AppId, ct)
            ?? throw DomainException.NotFound("no active rate lock");
        await locks.UnlockAsync(active.LockId, c.Reason, c.ActorId,
            clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), ct);
        await audit.WriteAsync(c.ActorId, "rate_unlock", "application",
            c.AppId, [], c.Reason, ct);
        return true;
    }
}
