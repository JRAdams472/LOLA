namespace Lola.Domain.Identity;

/// Fixed role set (ADR-0005). Stored as names in the control plane;
/// parsed at the repository boundary so code always sees the enum.
public enum LolaRole
{
    TenantAdmin,
    LoanOfficer,
    Borrower,
    Service
}

/// Role→field-class outcome: the only two states the visibility map
/// knows (ADR-0008). Masked is the default everywhere.
public enum FieldAccess
{
    Masked,
    Plaintext
}

public enum UserStatus
{
    Invited,
    Active,
    Disabled
}

/// Control-plane tenant registry row. `DbKeyWrapped` is the SQLCipher
/// file key as `vault:` transit ciphertext — never plaintext at rest.
public sealed record TenantRecord(
    string Id,
    string Slug,
    string DisplayName,
    string Status,
    string DbPath,
    string? DbKeyWrapped,
    string CreatedUtc);

public sealed record LolaUser(
    string Id,
    string TenantId,
    string Email,
    string DisplayName,
    string? PasswordHash,
    UserStatus Status,
    bool MfaEnabled,
    string CreatedUtc,
    string? MfaSecretRef = null);

public sealed record LolaGroup(
    string Id,
    string TenantId,
    string Name,
    string? Description,
    IReadOnlyList<LolaRole> Roles);

public sealed record InviteRecord(
    string Id,
    string TenantId,
    string Email,
    LolaRole Role,
    string TokenHash,
    string ExpiresUtc,
    string? UsedUtc,
    string CreatedBy);
