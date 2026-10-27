using Lola.Domain.Classification;
using Lola.Domain.Identity;

namespace Lola.Application.Identity;

/// Control-plane tenant registry (ADR-0003) — routing/provisioning
/// metadata only, never borrower PII.
public interface ITenantRegistry
{
    Task<TenantRecord?> FindByIdAsync(string id, CancellationToken ct = default);
    Task<TenantRecord?> FindBySlugAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken ct = default);

    /// Insert the registry row in `provisioning` status — first step of
    /// the provisioning pipeline; re-runs skip an existing row.
    Task InsertProvisioningAsync(string id, string slug, string displayName, string dbPath, CancellationToken ct = default);

    /// Store the transit-wrapped SQLCipher file key after the data
    /// plane file exists.
    Task SetDbKeyAsync(string tenantId, string dbKeyWrapped, CancellationToken ct = default);

    Task SetStatusAsync(string tenantId, string status, CancellationToken ct = default);
}

/// Control-plane user directory — auth identifiers only (MPE-67).
public interface IUserDirectory
{
    Task<LolaUser?> FindByIdAsync(string id, CancellationToken ct = default);

    /// Login-time lookup — email is unique PER TENANT, so a global
    /// email search can span tenants; dev login resolves the row and
    /// derives the tenant from it (multi-tenant same-email users get a
    /// tenant hint later).
    Task<LolaUser?> FindByEmailAsync(string email, CancellationToken ct = default);
    Task<IReadOnlyList<LolaUser>> ListByTenantAsync(string tenantId, CancellationToken ct = default);

    /// Effective roles = union over the user's groups' roles.
    Task<IReadOnlyList<LolaRole>> RolesForUserAsync(string userId, CancellationToken ct = default);

    Task InsertAsync(LolaUser user, CancellationToken ct = default);
    Task UpdatePasswordAsync(string userId, string passwordHash, string status, CancellationToken ct = default);
    Task SetStatusAsync(string userId, string status, CancellationToken ct = default);
}

/// Group directory over the control plane — group→role mapping lives
/// here (ADR-0005: roles come from groups, not per-user flags).
public interface IGroupDirectory
{
    Task<IReadOnlyList<LolaGroup>> ListByTenantAsync(string tenantId, CancellationToken ct = default);
    Task<LolaGroup?> FindByIdAsync(string id, CancellationToken ct = default);
    Task<int> MemberCountAsync(string groupId, CancellationToken ct = default);

    Task InsertAsync(LolaGroup group, CancellationToken ct = default);
    Task UpdateAsync(string id, string? name, string? description, CancellationToken ct = default);
    Task SetRolesAsync(string groupId, IReadOnlyCollection<LolaRole> roles, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);

    Task AddMemberAsync(string groupId, string userId, CancellationToken ct = default);
    Task RemoveMemberAsync(string groupId, string userId, CancellationToken ct = default);
}

/// Role → PII-class visibility map (role_visibility table, ADR-0008).
/// Effective visibility = union of plaintext grants across roles;
/// absence means masked.
public interface IRoleVisibilityMap
{
    Task<IReadOnlySet<PiiClass>> VisibleClassesAsync(
        IReadOnlyCollection<LolaRole> roles, CancellationToken ct = default);
}

/// Password hashing port — Argon2id (PHC strings) in Infrastructure.
public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string passwordHash);
}

/// Creates the tenant's SQLCipher data-plane file: generates the file
/// key, wraps it via the tenant's transit key, creates the keyed file
/// with hygiene pragmas + schema marker. Returns the `vault:`-wrapped
/// file key for the registry — the plaintext key never rests here.
public interface ITenantDbProvisioner
{
    Task<string> ProvisionFileAsync(TenantRecord tenant, CancellationToken ct = default);
}

/// Tenant provisioning job (MPE-67): registry row → transit keys →
/// SQLCipher file → downstream client creds (Phase 4 step). Steps are
/// idempotent — re-running after a failure resumes, not duplicates.
public interface ITenantProvisioner
{
    Task<ProvisionResult> ProvisionAsync(ProvisionRequest request, CancellationToken ct = default);
}

public sealed record ProvisionRequest(string TenantId, string Slug, string DisplayName);

public sealed record ProvisionResult(TenantRecord Tenant, IReadOnlyList<string> StepsCompleted);
