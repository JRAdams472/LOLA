using System.Security.Claims;
using Lola.Api.Identity;
using Lola.Application.Applications;
using Lola.Application.Interop;
using Lola.Application.Security;
using Lola.Domain.Identity;
using Microsoft.AspNetCore.Http;
using Wolverine;

namespace Lola.Api.Applications;

/// Intake surface (MPE-110) — `/api/tenants/{id}/applications[...]`.
/// The `{id}` path must match the resolved TenantContext (a crafted id
/// can never reach another tenant's file), and handlers re-guard so
/// GraphQL is covered too. The Applications policy admits all four
/// roles; borrower reach is own-draft-only inside the handlers.
public static class ApplicationEndpoints
{
    public static void MapApplicationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/tenants/{id}/applications")
            .RequireAuthorization(LolaPolicies.Applications);

        group.MapPost("", async (string id, CreateDraftRequest? body,
                ClaimsPrincipal user, IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<DraftCreatedResult>(
                new CreateDraftCommand(id, body?.BorrowerUserId,
                    Actor(user), IsBorrower(user)), ct)));

        group.MapGet("", async (string id, ClaimsPrincipal user,
                IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<IReadOnlyList<ApplicationMeta>>(
                new ListApplicationsQuery(id, Actor(user), IsBorrower(user)), ct)));

        group.MapGet("/search", async (string id, string field, string value,
                ClaimsPrincipal user, IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<ApplicationSearchResult>(
                new SearchApplicationsQuery(id, field, value,
                    Actor(user), IsBorrower(user)), ct)));

        group.MapGet("/{appId}", async (string id, string appId,
                ClaimsPrincipal user, IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<ApplicationDto?>(
                new GetApplicationQuery(id, appId, Actor(user), IsBorrower(user)), ct)));

        group.MapPatch("/{appId}", async (string id, string appId,
                UpdateDraftRequest body, ClaimsPrincipal user,
                IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<int>(
                new UpdateDraftCommand(id, appId, body.Patch,
                    Actor(user), IsBorrower(user)), ct)));

        group.MapPost("/{appId}/submit", async (string id, string appId,
                ClaimsPrincipal user, IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<SubmitResult>(
                new SubmitApplicationCommand(id, appId,
                    Actor(user), IsBorrower(user)), ct)));

        group.MapPost("/{appId}/correct", async (string id, string appId,
                UpdateDraftRequest body, ClaimsPrincipal user,
                IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<SubmitResult>(
                new CorrectApplicationCommand(id, appId, body.Patch,
                    Actor(user), IsBorrower(user)), ct)));

        group.MapPost("/{appId}/transition", async (string id, string appId,
                TransitionRequest body, ClaimsPrincipal user,
                IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<TransitionResult>(
                new TransitionApplicationCommand(id, appId, body.Target,
                    Actor(user), IsBorrower(user)), ct)));

        // Interop (MPE-111) — exports carry the real values (a masked
        // MISMO/ULDD file is useless); import always lands as a draft.
        group.MapGet("/{appId}/export-mismo", async (string id, string appId,
                ClaimsPrincipal user, IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<XmlResult>(
                new ExportMismoQuery(id, appId,
                    Actor(user), IsBorrower(user)), ct)));

        group.MapPost("/import-mismo", async (string id,
                ImportMismoRequest body, ClaimsPrincipal user,
                IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<MismoImportResult>(
                new ImportMismoCommand(id, body.Xml,
                    Actor(user), IsBorrower(user)), ct)));

        group.MapGet("/{appId}/export-uldd", async (string id, string appId,
                ClaimsPrincipal user, IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<XmlResult>(
                new ExportUlddQuery(id, appId,
                    Actor(user), IsBorrower(user)), ct)));

        // Pipeline step history (MPE-112) — append-only named-step log.
        group.MapGet("/{appId}/steps", async (string id, string appId,
                ClaimsPrincipal user, IMessageBus bus, CancellationToken ct) => await
            Guarded(id, async () => await bus.InvokeAsync<IReadOnlyList<StepEventDto>>(
                new GetApplicationStepsQuery(id, appId,
                    Actor(user), IsBorrower(user)), ct)));
    }

    /// Path tenant must equal the resolved scope — the middleware
    /// already bound TenantContext to the principal's `tid`.
    private static async Task<IResult> Guarded(string tenantId,
        Func<Task<object?>> work)
    {
        if (Lola.Application.Tenancy.TenantContext.Current?.TenantId != tenantId)
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                title: "request tenant does not match the resolved tenant");
        var result = await work();
        return result switch
        {
            SubmitResult { Ok: false } failed => Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "validation failed",
                extensions: new Dictionary<string, object?>
                { ["errors"] = failed.Errors }),
            XmlResult xml => Results.Text(xml.Xml, "application/xml"),
            _ => Results.Ok(result)
        };
    }

    private static string Actor(ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value ?? "unknown";

    private static bool IsBorrower(ClaimsPrincipal user) =>
        LolaPolicies.HasRole(user, LolaRole.Borrower);

    public sealed record CreateDraftRequest(string? BorrowerUserId);
    public sealed record UpdateDraftRequest(IReadOnlyDictionary<string, string> Patch);
    public sealed record TransitionRequest(string Target);
    public sealed record ImportMismoRequest(string Xml);
}
