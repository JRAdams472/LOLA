using Lola.Application.Admin;
using Lola.Application.Applications;

namespace Lola.Application.Interop;

/// Document interchange, not display — the exported MISMO message
/// carries the real field values (masking it would break the format),
/// so authorization is the policy + own-draft guard, not DocMasker.
public sealed record ExportMismoQuery(
    string TenantId, string AppId, string ActorId, bool ActorIsBorrower);

public sealed record XmlResult(string Xml);

public static class ExportMismoHandler
{
    public static async Task<XmlResult> Handle(
        ExportMismoQuery c, IApplicationRepository apps,
        Audit.ITenantAuditLog audit, CancellationToken ct)
    {
        AdminGuards.RequireTenant(c.TenantId);
        var app = await UpdateDraftHandler.RequireAppAsync(
            apps, c.AppId, c.ActorId, c.ActorIsBorrower, ct);
        await audit.WriteAsync(c.ActorId, "export", "application", c.AppId,
            Audit.AuditFields.InDoc(app.Doc), "mismo xml", ct);
        return new XmlResult(MismoXmlExporter.Export(app.Doc));
    }
}
