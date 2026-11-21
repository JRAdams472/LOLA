using System.Security.Claims;
using Lola.Api.Identity;
using Lola.Application.Applications;
using Lola.Application.Errors;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-272 — the scope discriminant fails closed: exactly one
/// recognized scope kind, a subject claim, and no mixed
/// staff/external role set. Anything else is denied, never the
/// old Staff fallback.
public class ScopeForTests
{
    [Theory]
    [InlineData("TenantAdmin", ActorScope.Staff)]
    [InlineData("LoanOfficer", ActorScope.Staff)]
    [InlineData("Service", ActorScope.Staff)]
    [InlineData("Broker", ActorScope.Partner)]
    [InlineData("Realtor", ActorScope.Partner)]
    [InlineData("Borrower", ActorScope.Borrower)]
    public void Single_kind_maps_to_scope(string role, ActorScope expected)
        => LolaPolicies.ScopeFor(Principal(role)).ShouldBe(expected);

    [Fact]
    public void Missing_subject_is_denied()
        => Should.Throw<DomainException>(() => LolaPolicies.ScopeFor(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("role", "LoanOfficer")]))));

    [Fact]
    public void No_recognized_role_is_denied()
        => Should.Throw<DomainException>(() => LolaPolicies.ScopeFor(
            Principal("Auditor", "lola-ops")));

    [Theory]
    [InlineData("TenantAdmin", "Broker")]
    [InlineData("LoanOfficer", "Realtor")]
    [InlineData("Service", "Borrower")]
    [InlineData("Broker", "Borrower")]
    public void Mixed_scope_kinds_are_denied(params string[] roles)
        => Should.Throw<DomainException>(
            () => LolaPolicies.ScopeFor(Principal(roles)));

    [Fact]
    public void Same_kind_multi_roles_are_allowed()
        => LolaPolicies.ScopeFor(Principal("Broker", "Realtor"))
            .ShouldBe(ActorScope.Partner);

    private static ClaimsPrincipal Principal(params string[] roles)
        => new(new ClaimsIdentity(
            new[] { new Claim("sub", "u-1") }
                .Concat(roles.Select(r => new Claim("role", r)))));
}
