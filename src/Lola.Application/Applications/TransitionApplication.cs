using Lola.Application.Admin;
using Lola.Application.Errors;
using Lola.Domain.Applications;

namespace Lola.Application.Applications;

/// Lifecycle transition — submitted→in_review→decisioned, staff only.
/// Updates the applications row only; version rows are untouched
/// (invariant 6). Pipeline position moves via the workflow seam
/// (MPE-112), not here.
public sealed record TransitionApplicationCommand(
    string TenantId, string AppId, string Target,
    string ActorId, bool ActorIsBorrower);

public sealed record TransitionResult(string Id, string Status);

public static class TransitionApplicationHandler
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["submitted"] = ["in_review"],
        ["in_review"] = ["decisioned"],
    };

    public static async Task<TransitionResult> Handle(
        TransitionApplicationCommand c, IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw DomainException.Forbidden("transitions are staff-only");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, false, ct);
        if (!Allowed.TryGetValue(app.Status, out var targets) || !targets.Contains(c.Target))
            throw DomainException.BadRequest(
                $"cannot transition '{app.Status}' → '{c.Target}'");
        await apps.UpdateStatusAsync(c.AppId, c.Target, ct);
        return new TransitionResult(c.AppId, c.Target);
    }
}
