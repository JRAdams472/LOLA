using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;

namespace Lola.Application.Pricing;

/// GET …/applications/{id}/lock (MPE-131) — the active lock, or null
/// (→ 404) when none is held. `GET …/locks` history is `ListAsync`.
public sealed record GetRateLockQuery(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class GetRateLockHandler
{
    public static async Task<RateLockDto?> Handle(
        GetRateLockQuery q, IApplicationRepository apps,
        IRateLockRepository locks, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope != ActorScope.Staff)
            throw DomainException.Forbidden("rate locks are staff-only");
        _ = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.Scope, ct);
        var active = await locks.GetActiveAsync(q.AppId, ct);
        return active is null ? null : RateLockDtos.ToDto(active);
    }
}
