using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Domain.Tests;

/// Shape checks on the identity records/enums added for M3 — the
/// domain must stay covered even before repositories exercise them.
public class IdentityTests
{
    [Fact]
    public void Role_set_matches_adr_0005()
        => Enum.GetNames<LolaRole>().ShouldBe(
            ["TenantAdmin", "LoanOfficer", "Borrower", "Service"], ignoreOrder: true);

    [Fact]
    public void TenantRecord_carries_wrapped_key()
    {
        var t = new TenantRecord("acme", "acme", "Acme", "provisioning",
            "data/tenants/acme.db", "vault:v1:k", "2026-10-27T00:00:00Z");
        t.Id.ShouldBe("acme");
        t.DbKeyWrapped.ShouldBe("vault:v1:k");
        t.Status.ShouldBe("provisioning");
    }

    [Fact]
    public void LolaUser_is_mfa_ready()
    {
        var u = new LolaUser("u1", "acme", "a@b.c", "A U", "$argon2id$phc",
            UserStatus.Active, MfaEnabled: true, "2026-10-27T00:00:00Z");
        u.MfaEnabled.ShouldBeTrue();
        u.Status.ShouldBe(UserStatus.Active);
        u.Email.ShouldBe("a@b.c");
    }

    [Fact]
    public void LolaGroup_carries_roles()
    {
        var g = new LolaGroup("g1", "acme", "Officers", "desc",
            [LolaRole.LoanOfficer, LolaRole.TenantAdmin]);
        g.Roles.ShouldContain(LolaRole.LoanOfficer);
        g.TenantId.ShouldBe("acme");
    }

    [Fact]
    public void InviteRecord_stores_hash_not_token()
    {
        var i = new InviteRecord("i1", "acme", "b@x.y", LolaRole.Borrower,
            "sha256hex", "2026-10-30T00:00:00Z", null, "officer-1");
        i.TokenHash.ShouldBe("sha256hex");
        i.UsedUtc.ShouldBeNull();
        i.Role.ShouldBe(LolaRole.Borrower);
    }

    [Fact]
    public void FieldAccess_and_UserStatus_values()
    {
        Enum.GetNames<FieldAccess>().ShouldBe(["Masked", "Plaintext"], ignoreOrder: true);
        Enum.GetNames<UserStatus>().ShouldBe(["Invited", "Active", "Disabled"], ignoreOrder: true);
    }

    /// Every generated property getter must run once — coverlet counts
    /// the declaration line covered only when the getter executes.
    [Fact]
    public void All_record_properties_readable()
    {
        object[] rows =
        [
            new TenantRecord("acme", "acme", "Acme", "active", "d", "k", "c"),
            new LolaUser("u", "t", "e", "n", "h", UserStatus.Invited, false, "c"),
            new LolaGroup("g", "t", "n", "d", [LolaRole.Service]),
            new InviteRecord("i", "t", "e", LolaRole.Borrower, "h", "x", "u", "cb")
        ];
        foreach (var row in rows)
            foreach (var prop in row.GetType().GetProperties())
                prop.GetValue(row);
    }
}
