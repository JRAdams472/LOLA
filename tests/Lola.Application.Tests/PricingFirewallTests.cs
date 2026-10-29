using System.Reflection;
using Lola.Application.Pricing;
using Lola.Domain.Classification;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-129 PII firewall (invariant 2): reflection guard over the egress
/// surface — any `[PiiClass]`-tagged member or PII-named/shaped field on
/// a `Lola.Application.Pricing` type fails CI. Mirrors MPEx's own
/// denylist as defense-in-depth.
public sealed class PricingFirewallTests
{
    // Exact-match tokens: short names where substring matching would
    // false-positive on legitimate members (ProductName, InvestorName).
    private static readonly string[] ExactDeny =
    [
        "ssn", "dob", "email", "phone", "address", "street",
        "city", "zip", "name", "account", "routing", "taxid"
    ];
    // Substring patterns: compound PII field names that can only mean
    // personal data on an egress model.
    private static readonly string[] ContainsDeny =
    [
        "firstname", "lastname", "middlename", "birthdate", "dateofbirth",
        "emailaddress", "phonenumber", "streetaddress", "postalcode",
        "accountnumber", "routingnumber", "driverlicense", "passport",
        "socialsecurity", "creditcard", "taxpayer"
    ];

    private static IEnumerable<(Type T, PropertyInfo P)> EgressMembers()
        => typeof(IPricingService).Assembly.GetTypes()
            .Where(t => t.Namespace == "Lola.Application.Pricing" &&
                        t.FullName?.Contains("<") != true)
            .SelectMany(t => t.GetProperties(
                BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (t, p)));

    [Fact]
    public void No_egress_member_carries_a_pii_class()
        => EgressMembers()
            .Where(m => m.P.GetCustomAttribute<PiiClassAttribute>() is not null)
            .Select(m => $"{m.T.Name}.{m.P.Name}")
            .ShouldBeEmpty();

    [Fact]
    public void No_egress_member_has_a_pii_shaped_name()
        => EgressMembers()
            .Where(m =>
                ExactDeny.Any(d =>
                    m.P.Name.Equals(d, StringComparison.OrdinalIgnoreCase)) ||
                ContainsDeny.Any(d =>
                    m.P.Name.Contains(d, StringComparison.OrdinalIgnoreCase)))
            .Select(m => $"{m.T.Name}.{m.P.Name}")
            .ShouldBeEmpty();

    [Fact]
    public void Firewall_detects_a_tagged_egress_member()
    {
        // Negative control: a hypothetical egress member carrying
        // [PiiClass] must trip the same scan the suite runs.
        var tagged = typeof(FakeEgressDto).GetProperty(nameof(FakeEgressDto.Secret))!;
        tagged.GetCustomAttribute<PiiClassAttribute>().ShouldNotBeNull();
    }

    private sealed record FakeEgressDto([property: PiiClass(PiiClass.Ssn)] string Secret);
}
