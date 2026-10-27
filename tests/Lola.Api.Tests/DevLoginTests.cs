using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-88: the dev login surface — the credential POST rides
/// auth-tight rate limits and returnUrl is HTML-encoded against
/// reflected XSS in the form.
public sealed class DevLoginTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Login_form_html_encodes_returnUrl()
    {
        var client = factory.CreateClient();
        var resp = await client.GetAsync(
            "/account/login?returnUrl=%22%3E%3Cscript%3Ealert(1)%3C/script%3E");
        var html = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("&quot;&gt;&lt;script&gt;");
        html.ShouldNotContain("\"><script>alert(1)</script>");
    }

    [Fact]
    public async Task Login_post_is_rate_limited()
    {
        // Dedicated factory: auth-tight partitions per IP+path and the
        // test host's null RemoteIpAddress shares one "unknown" bucket —
        // isolate so the limit is provable without starving siblings.
        using var isolated = new LolaApiFactory();
        var client = isolated.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            statuses.Add((await client.PostAsync("/account/login",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["email"] = "nobody@test.dev",
                    ["password"] = "wrong",
                    ["returnUrl"] = "/"
                }))).StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
    }
}
