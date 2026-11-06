using Lola.Application.Security;
using Lola.Infrastructure;
using Lola.Infrastructure.Crypto;
using Lola.Infrastructure.OpenBao;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Lola.Infrastructure.Tests;

/// Crypto DI wiring proof: AddLolaCrypto binds the OpenBao options
/// from env-named config keys and registers the transit-backed cipher
/// ports — a miswired registration fails at resolve time, which is
/// what this test makes loud.
public sealed class CryptoServiceCollectionTests
{
    [Fact]
    public void AddLolaCrypto_registers_options_and_cipher_ports()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OPENBAO_ADDR"] = "http://openbao.test:8200",
                ["OPENBAO_TOKEN_FILE"] = "/run/secret/token",
            }).Build();
        var services = new ServiceCollection();

        services.AddLolaCrypto(config);
        using var provider = services.BuildServiceProvider();

        var opts = provider.GetRequiredService<IOptions<OpenBaoOptions>>().Value;
        opts.Address.ShouldBe("http://openbao.test:8200");
        opts.TokenFile.ShouldBe("/run/secret/token");
        provider.GetRequiredService<IFieldCipher>()
            .ShouldBeOfType<TransitFieldCipher>();
        provider.GetRequiredService<IBlindIndex>()
            .ShouldBeOfType<TransitBlindIndex>();
        provider.GetRequiredService<ITenantKeyManager>()
            .ShouldBeOfType<TransitKeyManager>();
    }
}
