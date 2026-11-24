using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Lola.Api.Tests;

public class HealthTests(LolaApiFactory factory) : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Healthz_returns_ok()
    {
        var resp = await factory.CreateClient().GetAsync("/healthz");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_health_reports_service_and_db()
    {
        var body = await factory.CreateClient().GetStringAsync("/api/health");
        body.ShouldContain("lola");
        body.ShouldContain("\"db\":\"up\"");
    }

    [Fact]
    public async Task Readyz_reports_ready_in_dev()
    {
        // Dev shape: real migrated control db is ready; the OpenBao
        // value depends on ambient env (unconfigured/unsealed) so only
        // the env-independent checks are asserted.
        var body = await factory.CreateClient().GetStringAsync("/readyz");
        body.ShouldContain("\"status\":\"ready\"");
        body.ShouldContain("\"controlDb\":\"ok\"");
        body.ShouldContain("\"migrations\":\"current\"");
    }

    [Fact]
    public async Task Security_headers_present()
    {
        var resp = await factory.CreateClient().GetAsync("/healthz");
        resp.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
    }
}
