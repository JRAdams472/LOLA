using Lola.Application.Errors;

namespace Lola.Application.Applications;

/// Privileged-output operations gated beyond row scope (MPE-272).
/// Reaching an application row — the RequireAppAsync decision — does
/// not imply permission for every output derived from it.
public enum AppOperation
{
    /// Plaintext interchange output (MISMO). ULDD remains staff-only
    /// and keeps its own stricter check.
    Export,

    /// Generated-package registry reads and presigned downloads.
    PackageAccess,
}

/// The operation→scope matrix, deny-by-default: staff and the owning
/// borrower keep their existing contracts; a partner's whole reach is
/// the linked-app metadata projection plus the own-upload vault
/// surface, so plaintext exports and package reads are denied until a
/// kind-specific permission exists (MPE-267).
internal static class ApplicationOperations
{
    public static void Require(ActorScope scope, AppOperation operation)
    {
        var allowed = operation switch
        {
            AppOperation.Export or AppOperation.PackageAccess
                => scope is ActorScope.Staff or ActorScope.Borrower,
            _ => false,
        };
        if (!allowed)
            throw DomainException.Forbidden(
                $"operation '{operation}' is not available to this actor");
    }
}
