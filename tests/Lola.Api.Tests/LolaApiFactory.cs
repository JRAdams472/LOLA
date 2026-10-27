using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Lola.Api.Tests;

public class LolaApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CONTROL_DB_PATH"] = Path.Combine(
                    Path.GetTempPath(), "lola-tests", $"control-{Guid.NewGuid():N}.db")
            }));
}
