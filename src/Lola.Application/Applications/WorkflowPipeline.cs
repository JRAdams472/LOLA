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

public sealed class WorkflowPipeline(IApplicationRepository apps,
    Events.ILoanEventLog events,
    Tasks.TaskEvaluator eval) : IWorkflowPipeline
{
    public static readonly IReadOnlyList<string> Steps =
        ["POSIntake", "Validate", "PPE", "Fee", "Doc", "Package"];

    private static readonly string[] Order =
        ["POSIntake", "Validate", "PPE", "Fee", "Doc", "Package"];

    /// Steps whose handlers are real; everything later is seam-keyed.
    private const string LastRealStep = "Validate";

    /// Orthogonal vendor service steps (MPE-227): they don't sit in
    /// the linear Order — vendor orders run in parallel with the
    /// intake pipeline and report ordered/completed/failed as
    /// `application_step_events` under these names. The custom-
    /// workflow seam reads the same vocabulary.
    public static string ServiceStep(Lola.Application.Vendors.VendorKind
        kind) => kind switch
        {
            Lola.Application.Vendors.VendorKind.Credit => "Credit",
            Lola.Application.Vendors.VendorKind.Voe => "Voe",
            Lola.Application.Vendors.VendorKind.Voi => "Voi",
            Lola.Application.Vendors.VendorKind.Flood => "Flood",
            Lola.Application.Vendors.VendorKind.Fraud => "Fraud",
            Lola.Application.Vendors.VendorKind.Aus => "Aus",
            _ => throw Errors.DomainException.BadRequest(
                $"unknown vendor kind '{kind}'")
        };

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
        await events.WriteAsync(new Events.LoanEventDraft(app.Id,
            Events.LoanEventKind.Transition, actorId, "application",
            app.Id, "pipeline_step", null, app.PipelineStep, targetStep), ct);
        // Task queue re-derivation (M16): pipeline moves change which
        // step/trid/draft tasks should exist — evaluate best-effort
        // on the ambient tenant (middleware-scoped in requests).
        if (Tenancy.TenantContext.Current is { } scope)
            await eval.EvaluateApplicationAsync(
                scope.TenantId, app.Id, ct);
    }
}
