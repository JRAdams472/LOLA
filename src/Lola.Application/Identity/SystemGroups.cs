using Lola.Domain.Identity;

namespace Lola.Application.Identity;

/// Reserved system-group identities (MPE-273): the invite-acceptance
/// grant path resolves its destination by (tenant, system_kind), so an
/// editable group name can never redirect the grant. Names stay fixed
/// for discoverability but are protected against repurpose — ordinary
/// admin mutations reject them.
public static class SystemGroups
{
    public sealed record Kind(
        LolaRole Role, string SystemKind, string Name, string Description);

    public static readonly IReadOnlyList<Kind> All =
    [
        new(LolaRole.Borrower, "borrower", "borrowers",
            "Borrower portal users"),
        new(LolaRole.Broker, "broker", "brokers",
            "Broker partners (TPO)"),
        new(LolaRole.Realtor, "realtor", "realtors",
            "Realtor partners (TPO)"),
    ];

    /// The system kind backing an inviteable role, or null for roles
    /// that can never arrive through the public accept flow.
    public static Kind? ForRole(LolaRole role)
        => All.FirstOrDefault(k => k.Role == role);

    public static bool IsReservedName(string name)
        => All.Any(k => string.Equals(k.Name, name,
            StringComparison.OrdinalIgnoreCase));
}
