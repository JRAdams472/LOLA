using System.Reflection;
using System.Threading.RateLimiting;
using HotChocolate.AspNetCore;
using Lola.Api.Middleware;
using Lola.Api.Observability;
using Lola.ControlPlane;
using Lola.Infrastructure;
using Lola.Infrastructure.OpenBao;
using Microsoft.AspNetCore.OpenApi;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Sensitive;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) =>
{
    lc.ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithSensitiveDataMasking(o => o.MaskingOperators = Logging.MaskingOperators)
        .WriteTo.Console();
    var seqUrl = ctx.Configuration["SEQ_URL"];
    if (!string.IsNullOrWhiteSpace(seqUrl)) lc.WriteTo.Seq(seqUrl);
});

var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "lola"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation();
        // Exporter-free when the endpoint is unset — local runs emit nothing.
        if (!string.IsNullOrWhiteSpace(otlpEndpoint)) t.AddOtlpExporter();
    });

builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Lola.Application.Messaging.PingQuery).Assembly);
    opts.Policies.AddMiddleware<Lola.Application.Behaviors.LogExecution>();
});

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

// Resolved lazily so test/config overrides (merged during Build) apply.
builder.Services.AddSingleton(sp => new ControlPlaneDb(
    ControlPlaneDb.ResolvePath(sp.GetRequiredService<IConfiguration>()["CONTROL_DB_PATH"])));

builder.Services.AddLolaCrypto(builder.Configuration);

var app = builder.Build();
app.Services.GetRequiredService<ControlPlaneDb>().Migrate();
var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ProblemDetailsMiddleware>();
app.UseRateLimiter();
app.UseCors();

app.MapOpenApi();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/health", async (ControlPlaneDb db, OpenBaoTransitClient bao, CancellationToken ct) =>
{
    var dbUp = db.Healthy();
    var baoStatus = await bao.SealProbeAsync(ct);
    var up = dbUp && baoStatus is "unsealed" or "unconfigured";
    return up
        ? Results.Ok(new { service = "lola", version, db = "up", bao = baoStatus })
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
            title: dbUp ? $"openbao {baoStatus}" : "control-plane database unreachable");
});
app.MapGraphQL("/graphql");

app.Run();

public partial class Program;
