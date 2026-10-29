using Lola.Application.Admin;
using Lola.Application.Errors;
using Lola.Domain.Applications;

namespace Lola.Application.Applications;

/// Operator correction of a submitted/in-review application — staff
/// only, appends a NEW snapshot version (never an update; invariant 6)
/// after re-validating the corrected doc.
public sealed record CorrectApplicationCommand(
    string TenantId, string AppId,
    IReadOnlyDictionary<string, string> Patch,
    string ActorId, bool ActorIsBorrower);

public static class CorrectApplicationHandler
{
    private static readonly FieldCatalog Catalog = FieldCatalog.Current;

    public static async Task<SubmitResult> Handle(
        CorrectApplicationCommand c, IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.ActorIsBorrower)
            throw DomainException.Forbidden("correction is staff-only");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, false, ct);
        if (app.Status is "draft" or "decisioned")
            throw DomainException.BadRequest(
                $"corrections apply to submitted/in-review applications (status is '{app.Status}')");

        var doc = app.Doc;
        foreach (var (path, value) in c.Patch)
        {
            if (Catalog.SpecFor(path) is null)
                throw DomainException.BadRequest($"unknown field path '{path}'");
            doc.Set(path, value);
        }

        var errors = FieldValidator.Validate(doc);
        if (errors.Count > 0) return SubmitResult.Failed(errors);

        var version = await apps.WriteVersionAsync(
            c.AppId, doc, app.Status, c.ActorId, submitted: true, ct);
        return SubmitResult.Success(version);
    }
}
