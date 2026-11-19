using System.Reflection;
using Lola.Domain.Classification;
using Shouldly;

namespace Lola.Application.Tests;

/// M16 P1 (MPE-253) egress firewall for the task queue (invariants
/// 2/3): `tasks` rows live in the control plane and are an egress
/// boundary — every public member of every public type in
/// `Lola.Application.Tasks` must be non-PII (refs + operational
/// metadata only). Same deny vocabulary as the vendor/pricing
/// firewalls; a `[PiiClass]` member here would mean the boundary
/// itself is wrong, so those fail too.
public sealed class TaskFirewallTests
{
    private static readonly string[] ExactDeny =
    [
        "ssn", "dob", "email", "phone", "address", "street",
        "city", "zip", "name", "account", "routing", "taxid",
        "borrower", "detail", "description", "notes"
    ];
    private static readonly string[] ContainsDeny =
    [
        "firstname", "lastname", "middlename", "birthdate", "dateofbirth",
        "emailaddress", "phonenumber", "streetaddress", "postalcode",
        "accountnumber", "routingnumber", "driverlicense", "passport",
        "socialsecurity", "creditcard", "taxpayer"
    ];

    private static IEnumerable<Type> TaskTypes()
        => typeof(Tasks.ITaskRepository).Assembly.GetTypes()
            .Where(t => t.Namespace == "Lola.Application.Tasks"
                && t.FullName?.Contains("<") != true
                && t.IsClass && t.IsPublic);

    [Fact]
    public void Task_types_carry_no_pii_members()
        => TaskTypes()
            .SelectMany(t => t.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (T: t, P: p)))
            .Where(m =>
                m.P.GetCustomAttribute<PiiClassAttribute>() is not null ||
                ExactDeny.Any(d => m.P.Name.Equals(d,
                    StringComparison.OrdinalIgnoreCase)) ||
                ContainsDeny.Any(d => m.P.Name.Contains(d,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(m => $"{m.T.Name}.{m.P.Name}")
            .ShouldBeEmpty();
}
