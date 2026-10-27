using Lola.Application.Identity;

namespace Lola.DataPlane.Tests;

/// OIDC client provisioning is Api-layer; the provisioning service
/// only needs the port to call. The stub proves the step runs and its
/// result carries through to ProvisionResult.
internal sealed class StubServiceClients : IServiceClientProvisioner
{
    public Task<ServiceClientResult> EnsureAsync(string tenantId, CancellationToken ct = default)
        => Task.FromResult(new ServiceClientResult($"svc-{tenantId}", "test-secret"));
}
