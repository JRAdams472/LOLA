using Lola.Application.Security;
using Lola.Infrastructure.Crypto;
using Lola.Infrastructure.OpenBao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lola.Infrastructure;

/// Crypto service registration — transit client + cipher/index/key
/// ports. Singletons are safe: the per-tenant part rides CipherContext
/// (AsyncLocal), the services are stateless.
public static class CryptoServiceCollectionExtensions
{
    public static IServiceCollection AddLolaCrypto(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<OpenBaoOptions>(o =>
        {
            o.Address = config["OPENBAO_ADDR"];
            o.Token = config["OPENBAO_TOKEN"];
            o.TokenFile = config["OPENBAO_TOKEN_FILE"];
        });
        services.AddHttpClient<OpenBaoTransitClient>();
        services.AddSingleton<IFieldCipher, TransitFieldCipher>();
        services.AddSingleton<IBlindIndex, TransitBlindIndex>();
        services.AddSingleton<ITenantKeyManager, TransitKeyManager>();
        return services;
    }
}
