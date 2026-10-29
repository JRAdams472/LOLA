using Lola.Application.Admin;
using Lola.Application.Applications;

namespace Lola.Application.Interop;

/// ULDD structural export — same plaintext-document semantics as the
/// MISMO export (interchange format, authorized action).
public sealed record ExportUlddQuery(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower);

public static class ExportUlddHandler
{
    public static async Task<XmlResult> Handle(
        ExportUlddQuery q, IApplicationRepository apps, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.ActorIsBorrower, ct);
        return new XmlResult(UlddExporter.Export(app.Doc));
    }
}
