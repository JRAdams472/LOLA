using Lola.Application.Errors;
using Lola.Application.Identity;

namespace Lola.Application.Admin.Tenants;

public sealed record EraseTenantCommand(string TenantId);

/// Destructive tenant erasure (MPE-96): the handler guards what the
/// endpoint's Provision policy can't — the tenant must exist and must
/// not already be retired. Retention gating lands in M6; until then
/// this is an explicit operator action only.
public static class EraseTenantHandler
{
    public static async Task<TenantErasureResult> Handle(
        EraseTenantCommand c, ITenantRegistry registry, ITenantEraser eraser,
        CancellationToken ct)
    {
        var tenant = await registry.FindByIdAsync(c.TenantId, ct)
            ?? throw DomainException.NotFound("tenant not found");
        if (tenant.Status == "retired")
            throw DomainException.BadRequest("tenant is already retired");
        return await eraser.EraseAsync(tenant, ct);
    }
}
