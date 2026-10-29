using Lola.Application.Errors;

namespace Lola.Application.Applications;

/// Named-step workflow seam (MPE-112, ADR-0007): the origination
/// pipeline is `POSIntake → Validate → PPE → Fee → Doc → Package`.
/// Intake/Validate are real — submit advances a passing draft into
/// `Validate`; `PPE`/`Fee`/`Doc`/`Package` are registered step handlers
/// keyed for M7–M9 and reject with an audit event until then.
/// Every transition attempt is recorded in `application_step_events`.
public interface IWorkflowPipeline
{
    Task AdvanceAsync(ApplicationRecord app, string targetStep,
        string actorId, string reason, CancellationToken ct = default);
}

public sealed class WorkflowPipeline(IApplicationRepository apps)
    : IWorkflowPipeline
{
    public static readonly IReadOnlyList<string> Steps =
        ["POSIntake", "Validate", "PPE", "Fee", "Doc", "Package"];

    private static readonly string[] Order =
        ["POSIntake", "Validate", "PPE", "Fee", "Doc", "Package"];

    /// Steps whose handlers are real; everything later is seam-keyed.
    private const string LastRealStep = "Validate";

    public async Task AdvanceAsync(ApplicationRecord app, string targetStep,
        string actorId, string reason, CancellationToken ct = default)
    {
        var current = Array.IndexOf(Order, app.PipelineStep);
        var target = Array.IndexOf(Order, targetStep);
        if (target < 0)
            throw DomainException.BadRequest($"unknown pipeline step '{targetStep}'");
        if (target != current + 1)
            throw DomainException.BadRequest(
                $"pipeline moves one step forward ('{app.PipelineStep}' → '{targetStep}' rejected)");

        if (target > Array.IndexOf(Order, LastRealStep))
        {
            await apps.RecordStepEventAsync(app.Id, targetStep, "keyed",
                actorId, reason, ct);
            throw DomainException.BadRequest(
                $"pipeline step '{targetStep}' is seam-keyed for M7–M9");
        }
        await apps.UpdatePipelineStepAsync(app.Id, targetStep, ct);
        await apps.RecordStepEventAsync(app.Id, targetStep, "entered",
            actorId, reason, ct);
    }
}
