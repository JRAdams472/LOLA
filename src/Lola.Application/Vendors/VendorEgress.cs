namespace Lola.Application.Vendors;

/// Per-kind egress allowlist (invariant 2, MPE-77): the declared set
/// of members each `VendorOrderRequest` may carry — nothing more. The
/// reflection firewall asserts the concrete record's property set
/// equals this list exactly, in both directions: an undeclared member
/// on the record fails, and a declared member dropped from the record
/// fails. PII is legal here because these services cannot work
/// without it — the control is *minimum fields*, not *no fields*.
public static class VendorEgress
{
    public static readonly IReadOnlyDictionary<VendorKind, IReadOnlySet<string>>
        Allowed = new Dictionary<VendorKind, IReadOnlySet<string>>
        {
            [VendorKind.Credit] = new HashSet<string>(StringComparer.Ordinal)
            {
                "FirstName", "LastName", "Ssn", "Dob",
                "Street", "City", "State", "Zip"
            },
            [VendorKind.Voe] = new HashSet<string>(StringComparer.Ordinal)
            {
                "FirstName", "LastName", "Ssn",
                "EmployerName", "EmployerPhone"
            },
            [VendorKind.Voi] = new HashSet<string>(StringComparer.Ordinal)
            {
                "FirstName", "LastName", "Ssn", "EmployerName"
            },
            [VendorKind.Flood] = new HashSet<string>(StringComparer.Ordinal)
            {
                "Street", "City", "State", "Zip"
            },
            [VendorKind.Fraud] = new HashSet<string>(StringComparer.Ordinal)
            {
                "FirstName", "LastName", "Ssn", "Dob"
            },
            [VendorKind.Aus] = new HashSet<string>(StringComparer.Ordinal)
            {
                "ApplicationId", "Version", "MismoJson"
            },
        };
}
