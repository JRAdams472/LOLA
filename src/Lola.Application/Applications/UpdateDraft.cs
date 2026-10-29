using Lola.Application.Admin;
using Lola.Application.Errors;
using Lola.Domain.Applications;

namespace Lola.Application.Applications;

/// PATCH a draft by catalog path — every patch key must resolve to a
/// spec so callers can't smuggle unknown members into the payload.
public sealed record UpdateDraftCommand(
    string TenantId, string AppId,
    IReadOnlyDictionary<string, string> Patch,
    string ActorId, bool ActorIsBorrower);

public sealed record ApplicationUpdatedResult(string Id, int Version, string Status);

public static class UpdateDraftHandler
{
    private static readonly FieldCatalog Catalog = FieldCatalog.Current;

    public static async Task<int> Handle(
        UpdateDraftCommand c, IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var app = await RequireAppAsync(apps, c.AppId, c.ActorId, c.ActorIsBorrower, ct);
        if (app.Status != "draft")
            throw DomainException.BadRequest("only drafts can be patched");

        var doc = app.Doc;
        foreach (var (path, value) in c.Patch)
        {
            if (Catalog.SpecFor(path) is null)
                throw DomainException.BadRequest($"unknown field path '{path}'");
            doc.Set(path, value);
        }
        var version = await apps.WriteVersionAsync(
            c.AppId, doc, "draft", c.ActorId, submitted: false, ct);
        if (TridClock.Complete(doc))
            await apps.EnsureTridClockStartAsync(c.AppId, ct);
        return version;
    }

    internal static async Task<ApplicationRecord> RequireAppAsync(
        IApplicationRepository apps, string appId,
        string actorId, bool isBorrower, CancellationToken ct)
    {
        var app = await apps.GetAsync(appId, ct)
            ?? throw DomainException.NotFound("application not found");
        if (isBorrower && app.BorrowerUserId != actorId)
            throw DomainException.Forbidden("borrower scope covers own drafts only");
        return app;
    }
}
