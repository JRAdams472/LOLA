using System.Net;
using Lola.Api.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-89: production-config hardening — the OpenAPI document is
/// dev-only and the session cookie is Secure outside Development.
public sealed class ProductionConfigTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Dev_serves_openapi_document()
    {
        var resp = await factory.CreateClient().GetAsync("/openapi/v1.json");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Production_hides_openapi_document()
    {
        using var prod = factory.WithWebHostBuilder(
            b => b.UseEnvironment("Production"));
        var resp = await prod.CreateClient().GetAsync("/openapi/v1.json");
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Login_cookie_is_secure_only_outside_dev()
    {
        var dev = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ConnectEndpoints.LoginScheme);
        dev.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.SameAsRequest);

        using var prodFactory = factory.WithWebHostBuilder(
            b => b.UseEnvironment("Production"));
        _ = prodFactory.Server; // force host build so options resolve
        var prod = prodFactory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ConnectEndpoints.LoginScheme);
        prod.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.Always);
    }
}
