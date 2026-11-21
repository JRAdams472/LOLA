using System.Security.Claims;

namespace Lola.Api.Identity;

/// Platform authority is a dedicated claim issued only in the validated
/// client-credentials grant for explicitly designated global client
/// registrations (MPE-271). It can never be derived from a requested
/// scope, a role name, or editable group membership — group roles are
/// <see cref="Domain.Identity.LolaRole"/> values and cannot mint it.
/// Replacing the old Service-role shortcut means tokens issued before
/// this change lose fleet access at their natural expiry: reissue ops
/// credentials at cutover, never fall back to the bare Service role.
public static class PlatformAuthority
{
    /// Claim type carrying platform authority.
    public const string Claim = "lola_authority";

    /// The only authority value the PlatformOps policy accepts.
    public const string Value = "platform";

    /// Global client registrations designated for platform operations:
    /// the platform operator (`lola-ops`) and global automation
    /// (`lola-svc`). `svc-{tenant}` clients are never designated —
    /// their grants carry `tid` and tenant-only authority.
    public static readonly IReadOnlySet<string> DesignatedClientIds =
        new HashSet<string>(StringComparer.Ordinal) { "lola-ops", "lola-svc" };

    /// Platform principal = the explicit authority claim AND no tenant
    /// binding. A `tid` claim alongside the authority claim is a
    /// conflicting grant — fail closed rather than union privileges.
    public static bool IsPlatformPrincipal(ClaimsPrincipal user)
        => user.HasClaim(Claim, Value) && user.FindFirst("tid") is null;
}
