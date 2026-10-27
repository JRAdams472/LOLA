using System.Security.Cryptography;
using Lola.Application.Identity;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Lola.Api.Identity;

/// Creates the `svc-{tenant}` client-credentials OIDC client through
/// the manager (secret hashed before storage — the plaintext is
/// returned to the provisioning caller exactly once). Scoped manager
/// resolved per call so this can sit behind the singleton provisioner.
public sealed class TenantServiceClientProvisioner(IServiceProvider services)
    : IServiceClientProvisioner
{
    public async Task<ServiceClientResult> EnsureAsync(string tenantId, CancellationToken ct = default)
    {
        var clientId = $"svc-{tenantId}";
        await using var scope = services.CreateAsyncScope();
        var applications = scope.ServiceProvider
            .GetRequiredService<IOpenIddictApplicationManager>();

        if (await applications.FindByClientIdAsync(clientId, ct) is not null)
            return new ServiceClientResult(clientId, null);

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await applications.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = ClientTypes.Confidential,
            DisplayName = $"Service account — {tenantId}",
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + "lola-api"
            }
        }, ct);
        return new ServiceClientResult(clientId, secret);
    }
}
