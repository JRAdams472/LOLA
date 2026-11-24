using System.Net;
using Lola.Api.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-277: production-host proofs — an unsafe Production config fails
/// before serving, a valid disposable profile serves with secure
/// cookies + https contracts, and dependency loss reports not-ready
/// while liveness stays distinct.
public sealed class ProductionHostTests
{
    /// Prod host: the dev factory's hermetic stubs plus a production
    /// environment and a controllable OpenBao seal probe.
    private sealed class ProdApiFactory(bool baoSealed = false)
        : LolaApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment(Environments.Production);
            builder.ConfigureServices(services =>
            {
                // Hermetic seal probe — readiness must not depend on a
                // live vault in tests. Registered after the typed
                // client so it wins single-service resolution.
                services.AddSingleton(
                    new Lola.Infrastructure.OpenBao.OpenBaoTransitClient(
                        new HttpClient(new FakeBaoHandler(baoSealed)),
                        Options.Create(
                            new Lola.Infrastructure.OpenBao.OpenBaoOptions
                            { Address = "https://bao.test" })));
            });
        }
    }

    private sealed class FakeBaoHandler(bool sealed_) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"initialized":true,"sealed":{{sealed_.ToString().ToLowerInvariant()}},"standby":false}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public void Unsafe_production_config_fails_before_serving()
    {
        // No production profile env — every required key is missing;
        // the validator throws during host build, before any request.
        using var prod = new ProdApiFactory();
        var ex = Assert.Throws<InvalidOperationException>(
            () => prod.CreateClient());
        ex.Message.ShouldContain("Production configuration failed");
    }

    [Fact]
    public async Task Positive_profile_serves_https_and_secure_cookies()
    {
        using var env = ProdProfileEnv.Apply();
        await using var prod = new ProdApiFactory();
        var client = prod.CreateClient();

        (await client.GetAsync("/healthz")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        var ready = await client.GetAsync("/readyz");
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ready.Content.ReadAsStringAsync();
        body.ShouldContain("\"status\":\"ready\"");
        body.ShouldContain("\"openbao\":\"unsealed\"");
        body.ShouldContain("\"migrations\":\"current\"");

        // Session cookie is Secure-only in production.
        prod.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ConnectEndpoints.LoginScheme)
            .Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.Always);

        // Demo-identity seeders never run in Production.
        prod.Services.GetServices<IHostedService>()
            .ShouldNotContain(h => h is DevUserSeeder);

        // Registered callback origins are the configured https URIs.
        await using var scope = prod.Services.CreateAsyncScope();
        var apps = scope.ServiceProvider
            .GetRequiredService<IOpenIddictApplicationManager>();
        var console = await apps.FindByClientIdAsync(
            "lola-officer-console");
        console.ShouldNotBeNull();
        var desc = new OpenIddictApplicationDescriptor();
        await apps.PopulateAsync(desc, console);
        desc.RedirectUris.ShouldContain(
            new Uri("https://console.example.com/callback"));
    }

    [Fact]
    public async Task Disabled_capability_fails_closed_503()
    {
        using var env = ProdProfileEnv.Apply();
        await using var prod = new ProdApiFactory();
        _ = prod.Server;

        // The factory's ConfigureServices stubs replace the seam for
        // hermetic e2e — enumerate registrations to find what the
        // production adapter matrix actually registered.
        var disabled = prod.Services
            .GetServices<Lola.Application.Pricing.IPricingService>()
            .Single(s => s.Adapter == "disabled");
        var ex = await Assert.ThrowsAsync<
            Lola.Application.Errors.DomainException>(() =>
                disabled.EvaluateAsync(null!, null!, CancellationToken.None));
        ex.Status.ShouldBe(503);
        ex.Message.ShouldContain("disabled");
    }

    [Fact]
    public async Task Dependency_loss_reports_not_ready_liveness_distinct()
    {
        using var env = ProdProfileEnv.Apply();
        await using var prod = new ProdApiFactory(baoSealed: true);
        var client = prod.CreateClient();

        var ready = await client.GetAsync("/readyz");
        ready.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await ready.Content.ReadAsStringAsync();
        body.ShouldContain("\"status\":\"not-ready\"");
        body.ShouldContain("\"openbao\":\"sealed\"");

        // Liveness is a distinct, dependency-free check.
        (await client.GetAsync("/healthz")).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Forwarded_headers_cannot_spoof_rate_partition()
    {
        using var env = ProdProfileEnv.Apply();
        // TRUSTED_PROXIES unset — X-Forwarded-* is ignored entirely.
        await using var prod = new ProdApiFactory();
        var client = prod.CreateClient();

        // Spoof a different source IP per request; if forwarded headers
        // were honored the auth-tight per-IP partition would rotate and
        // never trip. Unhonored → the real connection partitions, so
        // the 10/min budget binds and 429 fires.
        HttpStatusCode last = default;
        for (var i = 0; i < 12; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "/connect/token")
            {
                Content = new FormUrlEncodedContent(
                    [new("grant_type", "client_credentials"),
                     new("client_id", "x"),
                     new("client_secret", "y")]),
            };
            req.Headers.Add("X-Forwarded-For", $"10.9.8.{i}");
            last = (await client.SendAsync(req)).StatusCode;
        }
        last.ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
