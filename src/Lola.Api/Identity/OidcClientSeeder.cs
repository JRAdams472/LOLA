using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Lola.Api.Identity;

/// Seeds the three dev OIDC clients at boot (idempotent — create only
/// when missing). Client secrets go through the manager, which hashes
/// them before storing; plaintext never touches the database.
public sealed class OidcClientSeeder(
    IServiceProvider services, IConfiguration config) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var applications = scope.ServiceProvider
            .GetRequiredService<IOpenIddictApplicationManager>();
        var scopes = scope.ServiceProvider
            .GetRequiredService<IOpenIddictScopeManager>();

        if (await scopes.FindByNameAsync("lola-api", cancellationToken) is null)
            await scopes.CreateAsync(new OpenIddictScopeDescriptor
            {
                Name = "lola-api",
                DisplayName = "LOLA tenant data-plane access",
                Resources = { "lola" }
            }, cancellationToken);

        await EnsurePublicClient(applications, "lola-officer-console",
            "Officer console", "http://localhost:3200/callback", cancellationToken);
        await EnsurePublicClient(applications, "lola-borrower-portal",
            "Borrower portal", "http://localhost:3300/callback", cancellationToken);

        // Service client — confidential; the secret comes from env and
        // is hashed by the manager (never stored plaintext).
        var svcSecret = config["OIDC_SEED_SVC_SECRET"];
        if (await applications.FindByClientIdAsync("lola-svc", cancellationToken) is null
            && !string.IsNullOrWhiteSpace(svcSecret))
        {
            await applications.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "lola-svc",
                ClientSecret = svcSecret,
                ClientType = ClientTypes.Confidential,
                DisplayName = "LOLA service account",
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.ClientCredentials,
                    Permissions.Scopes.Profile,
                    Permissions.Prefixes.Scope + "lola-api"
                }
            }, cancellationToken);
        }

        // Platform operator (MPE-91, folded into M7): the `lola-ops`
        // client-credentials principal that owns the global tenant
        // registry surface — scoped to ops routes only, no tenant data.
        var opsSecret = config["OIDC_SEED_OPS_SECRET"];
        if (await applications.FindByClientIdAsync("lola-ops", cancellationToken) is null
            && !string.IsNullOrWhiteSpace(opsSecret))
        {
            await applications.CreateAsync(new OpenIddictApplicationDescriptor
            {
                ClientId = "lola-ops",
                ClientSecret = opsSecret,
                ClientType = ClientTypes.Confidential,
                DisplayName = "LOLA platform operator",
                Permissions =
                {
                    Permissions.Endpoints.Token,
                    Permissions.GrantTypes.ClientCredentials,
                    Permissions.Prefixes.Scope + "lola-api"
                }
            }, cancellationToken);
        }
    }

    private static async Task EnsurePublicClient(
        IOpenIddictApplicationManager applications,
        string clientId, string displayName, string redirectUri, CancellationToken ct)
    {
        if (await applications.FindByClientIdAsync(clientId, ct) is not null) return;
        await applications.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            DisplayName = displayName,
            ConsentType = ConsentTypes.Implicit,
            RedirectUris = { new Uri(redirectUri) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.OpenId,
                Permissions.Prefixes.Scope + Scopes.Profile,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                Permissions.Prefixes.Scope + "lola-api"
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        }, ct);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
