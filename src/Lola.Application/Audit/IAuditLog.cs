namespace Lola.Application.Audit;

/// Audit log ports — two backings sharing HashChain mechanics.
/// Tenant-scoped PII activity lands in the tenant's own SQLCipher file
/// (TenantAuditLog); control-plane events — identity access, admin
/// actions, retention overrides, erasure — land in the control-plane
/// database and outlive tenant files by design (an in-file row would
/// be shredded with the data it describes).
public interface ITenantAuditLog
{
    Task WriteAsync(string actor, string action, string entity,
        string? entityId, IReadOnlyList<string> fieldClasses,
        string? reason, CancellationToken ct = default);
    Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditFilter filter,
        CancellationToken ct = default);
}

public interface IControlPlaneAuditLog
{
    Task WriteAsync(string actor, string action, string entity,
        string? entityId, IReadOnlyList<string> fieldClasses,
        string? reason, CancellationToken ct = default);
    Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditFilter filter,
        CancellationToken ct = default);
}
