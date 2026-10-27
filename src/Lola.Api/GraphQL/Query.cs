using Lola.Application.Identity;
using Lola.Application.Messaging;
using Lola.Application.Security;
using Wolverine;

namespace Lola.Api.GraphQL;

public class Query(IMessageBus bus)
{
    public Task<string> Ping(CancellationToken cancellationToken)
        => bus.InvokeAsync<string>(new PingQuery(), cancellationToken);

    /// Same masked invoke seam as REST /api/admin/tenants/{id}/users —
    /// masking is identical by construction (ADR-0011).
    public Task<IReadOnlyList<AdminUserDto>> TenantUsers(
        string tenantId, CancellationToken cancellationToken)
        => bus.InvokeMaskedAsync<IReadOnlyList<AdminUserDto>>(
            new GetTenantUsersQuery(tenantId), cancellationToken);
}
