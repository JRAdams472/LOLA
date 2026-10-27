using System.Reflection;
using System.Threading.RateLimiting;
using HotChocolate.AspNetCore;
using Lola.Api.Middleware;
using Microsoft.AspNetCore.OpenApi;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWolverine(opts =>
    opts.Policies.AddMiddleware<Lola.Application.Behaviors.LogExecution>());

builder.Services.AddOpenApi();
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Lola.Api.GraphQL.Query>()
    .AddMaxExecutionDepthRule(8)
    .ModifyRequestOptions(o => o.IncludeExceptionDetails = builder.Environment.IsDevelopment());

builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter("global", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 200,
            Window = TimeSpan.FromMinutes(1)
        }));
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var corsOrigin = builder.Configuration["CORS_ORIGIN"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (!string.IsNullOrWhiteSpace(corsOrigin))
        p.WithOrigins(corsOrigin).WithMethods("GET", "POST").WithHeaders("content-type", "authorization");
}));

var app = builder.Build();
var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ProblemDetailsMiddleware>();
app.UseRateLimiter();
app.UseCors();

app.MapOpenApi();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/health", () => Results.Ok(new { service = "lola", version }));
app.MapGraphQL("/graphql");

app.Run();

public partial class Program;
