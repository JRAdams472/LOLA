using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Domain.Applications;

namespace Lola.Application.Interop;

/// MISMO XML → a NEW draft — imported applications always enter the
/// draft lifecycle; validation gates them at submit like any draft.
/// Borrower actors get the draft attributed to themselves (M12 seam).
public sealed record ImportMismoCommand(
    string TenantId, string Xml, string ActorId, bool ActorIsBorrower);

public sealed record MismoImportResult(string Id, int ImportedFields);

public static class ImportMismoHandler
{
    public static async Task<MismoImportResult> Handle(
        ImportMismoCommand c, IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var (doc, imported) = MismoXmlImporter.Import(c.Xml);
        var id = $"app-{Guid.NewGuid():N}"[..20];
        var borrowerId = c.ActorIsBorrower ? c.ActorId : null;
        await apps.InsertAsync(id, doc, "draft", borrowerId, c.ActorId, ct);
        return new MismoImportResult(id, imported);
    }
}
