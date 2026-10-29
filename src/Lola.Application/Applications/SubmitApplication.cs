using Lola.Application.Admin;
using Lola.Application.Errors;
using Lola.Domain.Applications;

namespace Lola.Application.Applications;

/// Draft → submitted: full catalog validation gates the transition —
/// a failing submit stays a draft and returns FieldError[] for the
/// operator-correction loop; a passing submit stamps an immutable
/// snapshot version (invariant 6).
public sealed record SubmitApplicationCommand(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower);

public sealed record SubmitResult(bool Ok, int? Version, IReadOnlyList<FieldError> Errors)
{
    public static SubmitResult Success(int v) => new(true, v, []);
    public static SubmitResult Failed(IReadOnlyList<FieldError> e) => new(false, null, e);
}

public static class SubmitApplicationHandler
{
    public static async Task<SubmitResult> Handle(
        SubmitApplicationCommand c, IApplicationRepository apps,
        IWorkflowPipeline pipeline, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, c.ActorIsBorrower, ct);
        if (app.Status != "draft")
            throw DomainException.BadRequest($"only drafts submit (status is '{app.Status}')");

        var errors = FieldValidator.Validate(app.Doc);
        if (errors.Count > 0) return SubmitResult.Failed(errors);

        // All six TRID triggers are required fields — a passing submit
        // always stamps the clock (no-op if a PATCH already did).
        await apps.EnsureTridClockStartAsync(c.AppId, ct);
        var version = await apps.WriteVersionAsync(
            c.AppId, app.Doc, "submitted", c.ActorId, submitted: true, ct);
        await pipeline.AdvanceAsync(app, "Validate", c.ActorId, "submitted", ct);
        return SubmitResult.Success(version);
    }
}
