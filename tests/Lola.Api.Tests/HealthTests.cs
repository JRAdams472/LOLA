using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Lola.Api.Tests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Healthz_returns_ok()
    {
        var resp = await factory.CreateClient().GetAsync("/healthz");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Api_health_reports_service()
    {
        var body = await factory.CreateClient().GetStringAsync("/api/health");
        body.ShouldContain("lola");
    }

    [Fact]
    public async Task Security_headers_present()
    {
        var resp = await factory.CreateClient().GetAsync("/healthz");
        resp.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
    }
}
