using Lola.Application.Audit;
using Lola.Application.Documents;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.DataPlane;
using Lola.Infrastructure.Crypto;
using Lola.Infrastructure.Documents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lola.Api.Tests;

/// MPE-192 marquee factory — the base factory's fakes stay for the
/// intake/pricing/fee arc (proven in their own marquees), but every
/// doc-adjacent seam is re-registered as its REAL implementation,
/// resolved last-wins after the base ConfigureServices runs:
///
///   TransitKeyManager + TenantDbProvisioner + TenantMigrationRunner
///   → ops provisioning creates real OpenBao transit keys, a real
///     SQLCipher tenant file, and real migrations end to end.
///   IspprTenantProvisioner + IspprSessionClient → the vendor binding
///     is provisioned against LIVE ISPPr (client/customer/generate-
///     scoped key/4 template masters), key wrapped under the tenant
///     transit key — nothing about the vendor is stubbed.
///   DocRepository + TenantAuditLog → registry rows and the audit
///     spine land in the real tenant file.
///   IspprDocumentAdapter → generate/poll/download-route calls go to
///     the live engine; the marquee fetches the MinIO ZIP itself.
///
/// DOC_ADAPTER is deliberately NOT set: env vars are process-wide and
/// would leak the live ITenantDocProvisioner registration into other
/// concurrently-building factories, breaking their hermetic
/// provisioning. Direct registration keeps the blast radius at zero.
/// DOC_OUTBOX_ENABLED=false is safe to set — the hosted worker is
/// never load-bearing in tests (everything drains explicitly).
///
/// Never-skip env contract, same as IspprLiveFixture: the marquee
/// proves the live vendor round-trip, so it fails loudly without
/// ISPPR_* + OpenBao env rather than silently passing on stubs.
public class DocMarqueeFactory : LolaApiFactory
{
    private readonly string _ispprBase = Require("ISPPR_BASE_URL");
    private readonly string _adminEmail = Require("ISPPR_ADMIN_EMAIL");
    private readonly string _adminPassword = Require("ISPPR_ADMIN_PASSWORD");

    public DocMarqueeFactory()
    {
        Require("OPENBAO_ADDR");
        if (Environment.GetEnvironmentVariable("OPENBAO_TOKEN") is null
            && Environment.GetEnvironmentVariable("OPENBAO_TOKEN_FILE") is null)
            throw new InvalidOperationException(
                "OPENBAO_TOKEN or OPENBAO_TOKEN_FILE is required — the " +
                "marquee provisions real tenant transit keys and never skips.");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Eager env read, same reason as the OIDC key paths in the
        // base factory — the hosted worker is disabled so drains stay
        // deterministic; nothing test-side relies on it.
        Environment.SetEnvironmentVariable("DOC_OUTBOX_ENABLED", "false");

        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // Real provisioning crypto — transit keys + keyed file +
            // DbUp migrations run for real against OpenBao/SQLCipher.
            services.AddSingleton<ITenantKeyManager, TransitKeyManager>();
            services.AddSingleton<ITenantDbProvisioner, TenantDbProvisioner>();
            services.AddSingleton<ITenantMigrationRunner, TenantMigrationRunner>();

            // Real data plane end to end — applications, parties,
            // pricing evals, rate locks, fee evals, documents, and
            // the audit spine all live in the keyed tenant file.
            // (documents.application_id REFERENCES applications(id) —
            // a fake in-memory app store can't satisfy that FK, and
            // making the whole stack real is the honest marquee.)
            // External adapters stay stubs: Alt pricing/fee services
            // answer evals; only the doc vendor is live (M9's point).
            services.AddScoped<Application.Applications.IApplicationRepository,
                ApplicationRepository>();
            services.AddScoped<Application.Applications.IApplicationPartyRepository,
                ApplicationPartyRepository>();
            services.AddScoped<Application.Pricing.IPricingRepository,
                PricingRepository>();
            services.AddScoped<Application.Pricing.IRateLockRepository,
                RateLockRepository>();
            services.AddScoped<Application.Fees.IFeeRepository,
                FeeRepository>();
            // Production registers the pipeline scoped; the base
            // factory made it singleton to match the fake repos —
            // RemoveAll, not last-wins, because ValidateOnBuild checks
            // every descriptor, not just the one that resolves.
            services.RemoveAll<Application.Applications.IWorkflowPipeline>();
            services.AddScoped<Application.Applications.IWorkflowPipeline,
                Application.Applications.WorkflowPipeline>();
            services.AddScoped<IDocumentRepository, DocRepository>();
            services.AddScoped<ITenantAuditLog, TenantAuditLog>();

            // Live vendor seam — singleton construction rather than
            // typed AddHttpClient: Wolverine handler codegen rejects
            // factory-transient service locations, and these clients
            // are stateless besides the session client's bearer cache
            // (same shape IspprLiveFixture constructs).
            services.AddSingleton<IDocumentService>(sp =>
                new IspprDocumentAdapter(
                    new HttpClient(new HttpClientHandler
                    { AllowAutoRedirect = false })
                    { Timeout = TimeSpan.FromSeconds(30) },
                    new IspprDocOptions(_ispprBase),
                    sp.GetRequiredService<IDocumentCredentialSource>()));
            services.AddSingleton<IDocVendorAdmin>(new IspprSessionClient(
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
                new IspprAdminOptions(
                    _ispprBase, _adminEmail, _adminPassword)));
            services.AddSingleton<ITenantDocProvisioner, IspprTenantProvisioner>();
        });
    }

    private static string Require(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException(
            $"{key} is required — the doc marquee proves the live " +
            "ISPPr + OpenBao round-trip and never skips.");
}
