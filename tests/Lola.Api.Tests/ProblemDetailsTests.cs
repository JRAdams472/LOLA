using Lola.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Lola.Api.Tests;

public class ProblemDetailsTests
{
    [Fact]
    public async Task Unhandled_exception_returns_rfc9457_problem()
    {
        var middleware = new ProblemDetailsMiddleware(
            _ => throw new InvalidOperationException("boom"),
            NullLogger<ProblemDetailsMiddleware>.Instance);
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        ctx.Response.ContentType.ShouldStartWith("application/problem+json");
    }
}
