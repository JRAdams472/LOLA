using Microsoft.AspNetCore.Mvc;

namespace Lola.Api.Middleware;

/// Single exception → RFC 9457 problem-details mapping. Domain error
/// mapping lands here as domain errors appear; no stack traces escape.
public sealed class ProblemDetailsMiddleware(RequestDelegate next, ILogger<ProblemDetailsMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled {Path}", context.Request.Path.Value);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Internal server error",
                Type = "https://httpstatuses.com/500"
            });
        }
    }
}
