using Amazon.Runtime;
using Amazon.S3;
using Lola.Application.Audit;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Vault;
using Lola.DataPlane;
using Lola.Infrastructure.Crypto;
using Lola.Infrastructure.Vault;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lola.Api.Tests;

/// MPE-203 vault marquee factory — same shape as DocMarqueeFactory:
/// the base factory's fakes stay for intake (the vault arc never
/// touches pricing/fees/docs-vendor), while every vault-adjacent
/// seam is re-registered as its REAL implementation, resolved
/// last-wins after the base ConfigureServices runs:
///
///   TransitKeyManager + TenantDbProvisioner + TenantMigrationRunner
///   → ops provisioning creates real OpenBao transit keys, a real
///     SQLCipher tenant file, and real migrations end to end.
///   TenantVaultProvisioner → the `vault-bucket` step creates a real
///     MinIO bucket (lola-vault-<tid>) during provisioning.
///   VaultRepository + TenantAuditLog → registry rows and the audit
///     spine land in the real keyed tenant file.
///   TransitFieldCipher → filenames and wrapped DEKs are real transit
///     ciphertext, not the loopback fake.
///   MinioObjectVault + ClamAvScanner → uploads ride real MinIO and
///     the real clamd zINSTREAM path.
///   TransitVaultUrlSigner → download URLs are transit-HMACed under
///     the tenant's index key — the live sign/verify contract.
///
/// VAULT_ADAPTER is deliberately NOT set — env vars are process-wide
/// and would leak live registrations into other concurrently-built
/// factories. VAULT_SCAN_ENABLED=false is already set by the base
/// factory: drains are explicit through VaultScanProcessor.
///
/// Never-skip env contract, same as VaultLiveTests: the marquee
/// proves the live MinIO+ClamAV+OpenBao arc, so it fails loudly
/// without S3_*/CLAMAV_ADDR/OpenBao env rather than silently
/// passing on stubs.
public class VaultMarqueeFactory : LolaApiFactory
{
    private readonly string _s3Endpoint = Require("S3_ENDPOINT");
    private readonly string _s3Key = Require("S3_ACCESS_KEY");
    private readonly string _s3Secret = Require("S3_SECRET_KEY");
    private readonly string _clamAddr = Require("CLAMAV_ADDR");

    public VaultMarqueeFactory()
    {
        Require("OPENBAO_ADDR");
        if (Environment.GetEnvironmentVariable("OPENBAO_TOKEN") is null
            && Environment.GetEnvironmentVariable("OPENBAO_TOKEN_FILE") is null)
            throw new InvalidOperationException(
                "OPENBAO_TOKEN or OPENBAO_TOKEN_FILE is required — the " +
                "vault marquee provisions real tenant transit keys and " +
                "never skips.");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            // Real provisioning crypto + keyed tenant file + DbUp
            // migrations — identical to DocMarqueeFactory.
            services.AddSingleton<ITenantKeyManager, TransitKeyManager>();
            services.AddSingleton<ITenantDbProvisioner,
                TenantDbProvisioner>();
            services.AddSingleton<ITenantMigrationRunner,
                TenantMigrationRunner>();

            // Real data plane — vault_docs.application_id REFERENCES
            // applications(id), so app rows must land in the real
            // tenant file. Pricing/fee repos come along for the FK
            // surface; their services stay alt-stubs (not the arc).
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
            services.RemoveAll<Application.Applications.IWorkflowPipeline>();
            services.AddScoped<Application.Applications.IWorkflowPipeline,
                Application.Applications.WorkflowPipeline>();
            services.AddScoped<ITenantAuditLog, TenantAuditLog>();

            // The vault stack under test — every seam real.
            services.AddScoped<IVaultDocRepository, VaultRepository>();
            services.AddSingleton<IFieldCipher, TransitFieldCipher>();
            var s3 = new AmazonS3Client(
                new BasicAWSCredentials(_s3Key, _s3Secret),
                new AmazonS3Config
                {
                    ServiceURL = _s3Endpoint,
                    ForcePathStyle = true,
                    RequestChecksumCalculation =
                        RequestChecksumCalculation.WHEN_REQUIRED,
                    ResponseChecksumValidation =
                        ResponseChecksumValidation.WHEN_REQUIRED
                });
            services.AddSingleton<IAmazonS3>(s3);
            services.AddSingleton<IObjectVault>(new MinioObjectVault(s3));
            services.AddSingleton<IVirusScanner>(
                ClamAvScanner.Parse(_clamAddr));
            services.AddSingleton<ITenantVaultProvisioner>(
                new TenantVaultProvisioner(s3));
            services.AddSingleton<IVaultUrlSigner,
                TransitVaultUrlSigner>();
        });
    }

    private static string Require(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException(
            $"{key} is required — the vault marquee proves the live " +
            "MinIO+ClamAV+OpenBao arc and never skips.");
}
