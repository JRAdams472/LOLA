using Lola.Application.Admin;
using Lola.Application.Applications;
using Lola.Application.Errors;

namespace Lola.Application.Interop;

/// ULDD structural export — same plaintext-document semantics as the
/// MISMO export (interchange format, authorized action).
public sealed record ExportUlddQuery(
    string TenantId, string AppId, string ActorId, ActorScope Scope);

public static class ExportUlddHandler
{
    public static async Task<XmlResult> Handle(
        ExportUlddQuery q, IApplicationRepository apps,
        Audit.ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(q.TenantId);
        if (q.Scope != ActorScope.Staff)
            throw DomainException.Forbidden("ULDD export is staff-only");
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, q.AppId, q.ActorId, q.Scope, ct);
        await audit.WriteAsync(q.ActorId, "export", "application", q.AppId,
            Audit.AuditFields.InDoc(app.Doc), "uldd", ct);
        return new XmlResult(UlddExporter.Export(app.Doc));
    }
}
