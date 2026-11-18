using System.Reflection;
using Lola.Application.Vendors;
using Lola.Domain.Classification;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-226 egress firewall for the vendor seam (invariant 2): unlike
/// MPEx, vendor requests legitimately carry PII — the control is
/// minimum-fields-per-kind. Every VendorOrderRequest concrete record's
/// member set must equal `VendorEgress.Allowed[kind]` EXACTLY, in both
/// directions, and every PII member must carry its `[PiiClass]` tag so
/// masking/audit see the boundary. Metadata/envelope types in the
/// namespace keep the standard no-PII rule — their sensitive payload
/// lives inside `vault:` ciphertext or opaque `JsonObject` reports,
/// never in typed members.
public sealed class VendorFirewallTests
{
    // Exact-match + substring deny — same vocabulary as the pricing
    // firewall, applied to non-request types in the namespace.
    private static readonly string[] ExactDeny =
    [
        "ssn", "dob", "email", "phone", "address", "street",
        "city", "zip", "name", "account", "routing", "taxid"
    ];
    private static readonly string[] ContainsDeny =
    [
        "firstname", "lastname", "middlename", "birthdate", "dateofbirth",
        "emailaddress", "phonenumber", "streetaddress", "postalcode",
        "accountnumber", "routingnumber", "driverlicense", "passport",
        "socialsecurity", "creditcard", "taxpayer"
    ];

    // Declared non-PII members on request records — identifiers and
    // version markers that carry no personal data.
    private static readonly HashSet<string> NonPiiRequestMembers =
    [
        "AusOrderRequest.ApplicationId", "AusOrderRequest.Version"
    ];

    private static IEnumerable<Type> VendorTypes()
        => typeof(IVendorAdapter).Assembly.GetTypes()
            .Where(t => t.Namespace == "Lola.Application.Vendors"
                && t.FullName?.Contains("<") != true
                && t.IsClass && t.IsPublic);

    private static IEnumerable<Type> RequestTypes()
        => VendorTypes().Where(t =>
            !t.IsAbstract && typeof(VendorOrderRequest).IsAssignableFrom(t));

    // `Kind` is a compile-time constant on every concrete record, so
    // reading it off an uninitialized instance is safe — avoids
    // needing valid ctor args for value-type parameters.
    private static VendorKind KindOf(Type t)
        => ((VendorOrderRequest)System.Runtime.CompilerServices
            .RuntimeHelpers.GetUninitializedObject(t)).Kind;

    [Fact]
    public void Every_kind_has_a_request_record_and_an_allowlist()
    {
        var kinds = Enum.GetValues<VendorKind>();
        RequestTypes().Select(t => t.Name)
            .ShouldBeSubsetOf(
                kinds.Select(k => $"{k}OrderRequest").ToArray());
        RequestTypes().Select(t => KindOf(t)).ShouldBe(kinds.ToHashSet());
        VendorEgress.Allowed.Keys.ShouldBe(kinds.ToHashSet());
    }

    [Fact]
    public void Request_members_equal_the_kind_allowlist_exactly()
        => RequestTypes()
            .Select(t =>
            {
                var kind = KindOf(t);
                return new
                {
                    Type = t.Name,
                    Members = t.GetProperties(
                            BindingFlags.Public | BindingFlags.Instance)
                        .Select(p => p.Name)
                        .Where(n => n != nameof(VendorOrderRequest.Kind))
                        .ToHashSet(),
                    Allowed = VendorEgress.Allowed[kind]
                };
            })
            .Where(x => !x.Members.SetEquals(x.Allowed))
            .Select(x => $"{x.Type}: " +
                $"extra=[{string.Join(",", x.Members.Except(x.Allowed))}] " +
                $"missing=[{string.Join(",", x.Allowed.Except(x.Members))}]")
            .ShouldBeEmpty();

    [Fact]
    public void Every_request_pii_member_carries_a_pii_class_tag()
        => RequestTypes()
            .SelectMany(t => t.GetProperties(
                    BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (T: t, P: p)))
            .Where(m => m.P.Name != nameof(VendorOrderRequest.Kind)
                && !NonPiiRequestMembers.Contains($"{m.T.Name}.{m.P.Name}")
                && m.P.GetCustomAttribute<PiiClassAttribute>() is null)
            .Select(m => $"{m.T.Name}.{m.P.Name}")
            .ShouldBeEmpty();

    [Fact]
    public void Vendor_metadata_types_carry_no_pii_members()
        => VendorTypes()
            .Where(t => !typeof(VendorOrderRequest).IsAssignableFrom(t))
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
