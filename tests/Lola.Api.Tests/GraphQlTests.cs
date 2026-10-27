using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Lola.Api.Tests;

public class GraphQlTests(LolaApiFactory factory) : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Ping_routes_through_mediator()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync("/graphql",
            new { query = "{ ping }" });
        var body = await resp.Content.ReadAsStringAsync();
        body.ShouldContain("\"ping\":\"pong\"");
    }
}
