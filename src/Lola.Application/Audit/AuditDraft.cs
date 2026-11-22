namespace Lola.Application.Audit;

/// Parameters of one audit append, bundled so composite write ports
/// (MPE-274) can commit state + audit + loan event inside a single
/// tenant-file transaction. Same shape as ITenantAuditLog.WriteAsync.
public sealed record AuditDraft(
    string Actor, string Action, string Entity, string? EntityId,
    IReadOnlyList<string> FieldClasses, string? Reason);
