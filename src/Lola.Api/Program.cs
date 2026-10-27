using System.Reflection;
using System.Threading.RateLimiting;
using HotChocolate.AspNetCore;
using Lola.Api.Identity;
using Lola.Api.Middleware;
using Lola.Api.Observability;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.ControlPlane.OpenIddict;
using Lola.DataPlane;
using Lola.Infrastructure;
using Lola.Infrastructure.Identity;
using Lola.Infrastructure.OpenBao;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
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
builder.Services.AddSingleton<ITenantRegistry, TenantRegistry>();
builder.Services.AddSingleton<IUserDirectory, UserDirectory>();
builder.Services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();

// Identity — OpenIddict (ADR-0005): cookie login + OIDC server issuing
// opaque access tokens + signed id_tokens; the api validates its own
// tokens via UseLocalServer. Stores are the custom Dapper impls.
builder.Services.AddAuthorization();
builder.Services.AddAuthentication(
        OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
    .AddCookie(ConnectEndpoints.LoginScheme, o =>
    {
        o.Cookie.Name = "lola.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.SlidingExpiration = true;
    });
builder.Services.AddOpenIddict()
    .AddCore(options =>
    {
        options.SetDefaultApplicationEntity<LolaApplication>()
               .SetDefaultAuthorizationEntity<LolaAuthorization>()
               .SetDefaultScopeEntity<LolaScope>()
               .SetDefaultTokenEntity<LolaToken>();
        options.Services.AddScoped<IOpenIddictApplicationStore<LolaApplication>, DapperOpenIddictApplicationStore>();
        options.Services.AddScoped<IOpenIddictAuthorizationStore<LolaAuthorization>, DapperOpenIddictAuthorizationStore>();
        options.Services.AddScoped<IOpenIddictScopeStore<LolaScope>, DapperOpenIddictScopeStore>();
        options.Services.AddScoped<IOpenIddictTokenStore<LolaToken>, DapperOpenIddictTokenStore>();
    })
    .AddServer(options =>
    {
        options.SetAuthorizationEndpointUris("/connect/authorize")
               .SetTokenEndpointUris("/connect/token")
               .SetUserInfoEndpointUris("/connect/userinfo")
               .SetEndSessionEndpointUris("/connect/logout")
               .SetRevocationEndpointUris("/connect/revoke");
        options.RegisterScopes(
            OpenIddictConstants.Scopes.OpenId,
            OpenIddictConstants.Scopes.Profile,
            OpenIddictConstants.Scopes.Email,
            OpenIddictConstants.Scopes.Roles,
            OpenIddictConstants.Scopes.OfflineAccess,
            "lola-api");
        options.AllowAuthorizationCodeFlow()
               .RequireProofKeyForCodeExchange()
               .AllowClientCredentialsFlow()
               .AllowRefreshTokenFlow();
        options.SetAccessTokenLifetime(TimeSpan.FromMinutes(30))
               .SetRefreshTokenLifetime(TimeSpan.FromDays(14))
               // Reuse detection is an M3 acceptance criterion — a
               // redeemed refresh token must be rejected immediately.
               // (OpenIddict's 30s default exists to absorb concurrent
               // SPA refresh races; LOLA clients retry instead.)
               .SetRefreshTokenReuseLeeway(TimeSpan.Zero);
        options.UseReferenceAccessTokens()
               .DisableAccessTokenEncryption();
        options.AddSigningKey(DevSigningKey.LoadOrCreate(
            builder.Configuration["OIDC_SIGNING_KEY_PATH"] ?? "data/oidc/signing.json"));
        // Codes and refresh tokens are always encrypted payloads —
        // OpenIddict requires an encryption credential even when
        // access tokens are opaque references. Same persisted-dev-key
        // pattern; a distinct file + KeyId from the signing key.
        options.AddEncryptionKey(DevSigningKey.LoadOrCreate(
            builder.Configuration["OIDC_ENCRYPTION_KEY_PATH"] ?? "data/oidc/encryption.json",
            "lola-dev-encryption"));
        options.UseAspNetCore()
               .EnableAuthorizationEndpointPassthrough()
               .EnableTokenEndpointPassthrough()
               .EnableUserInfoEndpointPassthrough()
               .EnableEndSessionEndpointPassthrough()
               // Kestrel sees plain HTTP in dev/tests and behind a
               // TLS-terminating proxy — TLS is enforced at the edge
               // (invariant 5), not in the OIDC handler itself.
               .DisableTransportSecurityRequirement();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });
builder.Services.AddHostedService<OidcClientSeeder>();
builder.Services.AddSingleton<ITenantDbProvisioner, TenantDbProvisioner>();
builder.Services.AddScoped<TenantConnectionFactory>();
builder.Services.AddSingleton<ITenantProvisioner>(sp => new TenantProvisioningService(
    sp.GetRequiredService<ITenantRegistry>(),
    sp.GetRequiredService<ITenantKeyManager>(),
    sp.GetRequiredService<ITenantDbProvisioner>(),
    builder.Configuration["TENANT_DATA_DIR"] ?? "data/tenants"));

var app = builder.Build();
app.Services.GetRequiredService<ControlPlaneDb>().Migrate();
var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ProblemDetailsMiddleware>();
app.UseRateLimiter();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

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
app.MapConnectEndpoints();

app.Run();

public partial class Program;
