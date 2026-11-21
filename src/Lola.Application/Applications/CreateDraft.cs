using Lola.Application.Admin;
using Lola.Application.Errors;
using Lola.Domain.Applications;
using Lola.Domain.Identity;

namespace Lola.Application.Applications;

/// `actorIsBorrower` forces the draft to belong to that user (the
/// own-draft seam M12 rides); staff may attribute a borrower or none.
public sealed record CreateDraftCommand(
    string TenantId, string? BorrowerUserId,
    string ActorId, ActorScope Scope);

public sealed record DraftCreatedResult(string Id, string Status);

public static class CreateDraftHandler
{
    public static async Task<DraftCreatedResult> Handle(
        CreateDraftCommand c, IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        if (c.Scope == ActorScope.Partner)
            throw DomainException.Forbidden(
                "partners cannot originate drafts");
        var borrowerId = c.Scope == ActorScope.Borrower
            ? c.ActorId : c.BorrowerUserId;
        var id = $"app-{Guid.NewGuid():N}"[..20];
        await apps.InsertAsync(id, ApplicationDocument.Empty(),
            "draft", borrowerId, c.ActorId, ct);
        return new DraftCreatedResult(id, "draft");
    }
}
