using Dapper;

namespace Lola.ControlPlane;

/// One invalid group-role grant: a group_roles row carrying a role
/// outside the human-assignable set. Identifiers only — group/tenant
/// ids and role names are safe for logs.
public sealed record InvalidRoleGrant(string TenantId, string GroupId, string Role);

/// MPE-271 reconciliation scan. `Service` is machine authority minted
/// by the token endpoint; the group_roles CHECK predates that
/// restriction, so legacy rows may exist. Group create/update now
/// rejects machine roles, and the token claim — not the row — is the
/// enforcement point, but pre-existing grants still confer
/// Service-scoped surfaces on members. Report them at startup for
/// operator reconciliation; the scan never mutates the store.
public static class InvalidRoleGrantScan
{
    private static readonly string[] HumanAssignable =
        ["TenantAdmin", "LoanOfficer", "Borrower", "Broker", "Realtor"];

    public static IReadOnlyList<InvalidRoleGrant> Run(ControlPlaneDb db)
    {
        using var conn = db.Open();
        return conn.Query<InvalidRoleGrant>("""
            SELECT g.tenant_id AS TenantId, gr.group_id AS GroupId, gr.role AS Role
            FROM group_roles gr JOIN groups g ON g.id = gr.group_id
            WHERE gr.role NOT IN @human
            """, new { human = HumanAssignable }).AsList();
    }
}
