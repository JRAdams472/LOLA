using Lola.Application.Admin;
using Lola.Application.Errors;
using Lola.Domain.Applications;

namespace Lola.Application.Applications;

/// PATCH a draft by catalog path — every patch key must resolve to a
/// spec so callers can't smuggle unknown members into the payload.
public sealed record UpdateDraftCommand(
    string TenantId, string AppId,
    IReadOnlyDictionary<string, string> Patch,
    string ActorId, ActorScope Scope);

public sealed record ApplicationUpdatedResult(string Id, int Version, string Status);

public static class UpdateDraftHandler
{
    private static readonly FieldCatalog Catalog = FieldCatalog.Current;

    public static async Task<int> Handle(
        UpdateDraftCommand c, IApplicationRepository apps,
        Audit.ITenantAuditLog audit,
        Pricing.IRateLockRepository locks, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope == ActorScope.Partner)
            throw DomainException.Forbidden(
                "partners cannot patch application drafts");
        var app = await RequireAppAsync(apps, c.AppId, c.ActorId, c.Scope, ct);
        if (app.Status != "draft")
            throw DomainException.BadRequest("only drafts can be patched");
        await Pricing.RateFreeze.EnforceAsync(locks, c.AppId, c.Patch.Keys, ct);

        foreach (var path in c.Patch.Keys)
            if (Catalog.SpecFor(path) is null)
                throw DomainException.BadRequest($"unknown field path '{path}'");
        var (version, doc) = await apps.PatchDraftAsync(
            c.AppId, c.Patch, c.ActorId, ct);
        if (TridClock.Complete(doc))
            await apps.EnsureTridClockStartAsync(c.AppId, ct);
        await audit.WriteAsync(c.ActorId, "write", "application", c.AppId,
            Audit.AuditFields.ForPaths(c.Patch.Keys), "draft patch", ct);
        return version;
    }

    /// Row-scope guard (M19): every app-bound handler funnels here.
    /// Borrower reach is `BorrowerUserId == actor`; Partner reach is an
    /// active `application_partners` link — unlinked is 403, not 404,
    /// so link state is never enumerable through response shape.
    internal static async Task<ApplicationRecord> RequireAppAsync(
        IApplicationRepository apps, string appId,
        string actorId, ActorScope scope, CancellationToken ct)
    {
        var app = await apps.GetAsync(appId, ct)
            ?? throw DomainException.NotFound("application not found");
        if (scope == ActorScope.Borrower && app.BorrowerUserId != actorId)
            throw DomainException.Forbidden("borrower scope covers own applications only");
        if (scope == ActorScope.Partner
            && !await apps.IsPartnerLinkedAsync(appId, actorId, ct))
            throw DomainException.Forbidden("partner scope covers linked applications only");
        return app;
    }
}
