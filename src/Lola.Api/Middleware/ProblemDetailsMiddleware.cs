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
        catch (ArgumentException aex)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = aex.Message,
                Type = "https://httpstatuses.com/400"
            }, options: null, contentType: "application/problem+json");
        }
        catch (Application.Errors.DomainException dex)
        {
            context.Response.StatusCode = dex.Status;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = dex.Status,
                Title = dex.Message,
                Type = $"https://httpstatuses.com/{dex.Status}"
            }, options: null, contentType: "application/problem+json");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled {Path}", context.Request.Path.Value);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Internal server error",
                Type = "https://httpstatuses.com/500"
            }, options: null, contentType: "application/problem+json");
        }
    }
}
