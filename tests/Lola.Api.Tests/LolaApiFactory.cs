using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Lola.Api.Tests;

public class LolaApiFactory : WebApplicationFactory<Program>
{
    /// Host builds read process env vars eagerly (OIDC key paths,
    /// adapter switches, the MPE-277 production profile) — so
    /// ProdProfileEnv must mutate them under a write gate while every
    /// host build takes a read slot. Without this, a prod host test's
    /// env leaks into concurrently-built dev hosts and silently
    /// changes seeded callback URIs / adapter registrations.
    /// SemaphoreSlim + AsyncLocal, not ReaderWriterLockSlim: write
    /// holds span awaits, so release lands on a different thread.
    private const int EnvGateSlots = 64;
    private static readonly SemaphoreSlim EnvGate =
        new(EnvGateSlots, EnvGateSlots);
    private static readonly AsyncLocal<bool> EnvWriter = new();
    /// Writers drain the 64 read slots one at a time; two overlapping
    /// writers would each hold a subset and deadlock on the rest.
    /// This mutex serializes acquisition so only one drain is ever
    /// in flight — parallel prod-profile tests queue instead of
    /// interleaving.
    private static readonly SemaphoreSlim EnvWriterMutex = new(1, 1);

    /// Take every read slot — no host build may proceed until the
    /// returned token is disposed. Marks the ambient async context so
    /// the holder's OWN host builds (which would deadlock waiting for
    /// a slot) pass through.
    internal static IDisposable AcquireEnvWriteLock()
    {
        EnvWriterMutex.Wait();
        try
        {
            for (var i = 0; i < EnvGateSlots; i++) EnvGate.Wait();
        }
        catch
        {
            EnvWriterMutex.Release();
            throw;
        }
        EnvWriter.Value = true;
        return new WriteRelease();
    }

    private sealed class WriteRelease : IDisposable
    {
        public void Dispose()
        {
            EnvWriter.Value = false;
            EnvGate.Release(EnvGateSlots);
            EnvWriterMutex.Release();
        }
    }

    private sealed class ReadRelease : IDisposable
    {
        public void Dispose() => EnvGate.Release();
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }

    /// Hold one read slot while reading process env directly —
    /// live-test fixtures read OPENBAO/S3/ISPPR env outside any host
    /// build and must not observe a partially-applied production
    /// matrix either.
    internal static IDisposable EnvReadScope()
    {
        if (EnvWriter.Value) return Noop.Instance;
        EnvGate.Wait();
        return new ReadRelease();
    }

    // Every entry point (CreateClient/Server/Services) funnels through
    // EnsureServer → CreateHost → builder.Build() → Program.cs
    // top-level — where env vars are read. One read slot here covers
    // all of them; the active writer bypasses (it holds them all).
    protected override IHost CreateHost(IHostBuilder builder)
    {
        if (EnvWriter.Value) return base.CreateHost(builder);
        EnvGate.Wait();
        try { return base.CreateHost(builder); }
        finally { EnvGate.Release(); }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The signing-key path is read eagerly during service
        // registration — before ConfigureAppConfiguration merges —
        // so it must arrive via a real env var, not an in-memory
        // override (env providers load at CreateBuilder time).
        Environment.SetEnvironmentVariable("OIDC_SIGNING_KEY_PATH",
            Path.Combine(Path.GetTempPath(), "lola-tests", $"oidc-{Guid.NewGuid():N}.json"));
        Environment.SetEnvironmentVariable("OIDC_ENCRYPTION_KEY_PATH",
            Path.Combine(Path.GetTempPath(), "lola-tests", $"oidc-enc-{Guid.NewGuid():N}.json"));
        // Read eagerly during service registration like the OIDC key
        // paths — must be a real env var, not an in-memory override.
        // Tests drain PricingOutboxProcessor explicitly.
        Environment.SetEnvironmentVariable("PRICING_OUTBOX_ENABLED", "false");
        // Same eager-read reason — tests drain FeeOutboxProcessor
        // explicitly, never the hosted worker.
        Environment.SetEnvironmentVariable("FEE_OUTBOX_ENABLED", "false");
        // Vault scans drain explicitly too — the always-clean stub
        // scanner would otherwise race the `quarantined` assertions.
        Environment.SetEnvironmentVariable("VAULT_SCAN_ENABLED", "false");
        // Vendor outbox drains explicitly (MPE-227).
        Environment.SetEnvironmentVariable("VENDOR_OUTBOX_ENABLED", "false");

        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CONTROL_DB_PATH"] = Path.Combine(
                    Path.GetTempPath(), "lola-tests", $"control-{Guid.NewGuid():N}.db"),
                // Raw-hex dev key — the whole API e2e (migrations, OIDC
                // stores, health, seeders) runs on a KEYED control-plane
                // file (MPE-276 / Phase 7b).
                ["CONTROL_DB_KEY"] = TestDbKeys.ControlFileKeyHex,
                ["OIDC_SEED_SVC_SECRET"] = "svc-secret-value",
                ["OIDC_SEED_OPS_SECRET"] = "ops-secret-value",
                ["OIDC_BFF_CONSOLE_SECRET"] = "bff-console-secret-value",
                ["OIDC_BFF_PORTAL_SECRET"] = "bff-portal-secret-value",
                // The session store's self-calls (refresh/revoke) ride
                // the in-memory TestServer — see the handler override
                // below; the base only shapes the request URI.
                ["BFF_API_BASE"] = "http://localhost",
                ["TENANT_DATA_DIR"] = Path.Combine(
                    Path.GetTempPath(), "lola-tests", "tenants")
            }));

        // Provisioning's crypto steps are proven against real OpenBao in
        // Lola.DataPlane.Tests; here they are stubbed so the API e2e is
        // hermetic. OIDC client provisioning stays real (control-plane).
        builder.ConfigureServices(services =>
        {
            // Weak work factor everywhere — hashing is a fixed cost
            // the suite pays ~124x and asserts nothing about (MPE-269).
            services.AddSingleton<IPasswordHasher>(new TestPasswordHasher());
            services.AddSingleton<ITenantKeyManager>(new FakeKeyManager());
            services.AddSingleton<ITenantMigrationRunner>(new FakeMigrationRunner());
            services.AddSingleton<ITenantBackup>(new FakeTenantBackup());
            // REAL eraser, not a stub: key destruction is faked (no
            // OpenBao) but file deletion and the control-plane residual
            // lifecycle run for real — the M6 marquee asserts invites/
            // OIDC rows/groups die and users anonymize on erase.
            services.AddSingleton<ITenantEraser>(sp => new TenantErasureService(
                sp.GetRequiredService<ITenantKeyManager>(),
                sp.GetRequiredService<ITenantRegistry>(),
                sp.GetRequiredService<IControlPlaneResiduals>(),
                Path.Combine(Path.GetTempPath(), "lola-tests", "backups")));
            services.AddSingleton<ITenantDbProvisioner>(
                new FakeTenantDbProvisioner(Path.Combine(
                    Path.GetTempPath(), "lola-tests", "tenants")));
            // Intake surface (MPE-110) — handler shape + masking + role
            // scoping verified hermetic; the real repos are proven in
            // Lola.DataPlane.Tests.
            services.AddSingleton(new FakeApplicationStore());
            services.AddSingleton<Lola.Application.Applications.IApplicationRepository,
                FakeApplicationRepository>();
            services.AddSingleton<Lola.Application.Applications.IApplicationPartyRepository,
                FakeApplicationPartyRepository>();
            services.AddSingleton<Lola.Application.Applications.IApplicationPartnerRepository,
                FakeApplicationPartnerRepository>();
            services.AddScoped<Lola.Application.Applications.IWorkflowPipeline,
                Lola.Application.Applications.WorkflowPipeline>();
            services.AddSingleton(new FakeTenantAuditLog());
            services.AddSingleton<Lola.Application.Audit.ITenantAuditLog>(
                sp => sp.GetRequiredService<FakeTenantAuditLog>());
            // Mutation log + conditions (M15) — hermetic mirrors; the
            // SQLCipher impls are proven in Lola.DataPlane.Tests.
            services.AddSingleton(new FakeLoanEventLog());
            services.AddSingleton<Lola.Application.Events.ILoanEventLog>(
                sp => sp.GetRequiredService<FakeLoanEventLog>());
            services.AddSingleton<Lola.Application.Events
                .IConditionRepository, FakeConditionRepository>();
            // Task queue derivation (M16): real evaluator over the
            // fakes + the real TaskQueue on the temp control.db —
            // queue rows are inspectable in tests. Scoped because the
            // marquee factories swap in scoped real repos.
            services.AddScoped<Lola.Application.Tasks
                .TaskEvaluator>();
            // Pricing (MPE-130) — hermetic store + the swap-proof
            // second adapter: implementing IPricingService is the
            // whole contract, so this registration replaces the
            // production stub with zero other changes.
            services.AddSingleton(new FakePricingRepository());
            services.AddSingleton<Lola.Application.Pricing.IPricingRepository>(
                sp => sp.GetRequiredService<FakePricingRepository>());
            services.AddSingleton(new FakeRateLockRepository());
            services.AddSingleton<Lola.Application.Pricing.IRateLockRepository>(
                sp => sp.GetRequiredService<FakeRateLockRepository>());
            services.AddSingleton<Lola.Application.Pricing.IPricingService,
                AltStubPricingAdapter>();
            // Fees (MPE-183) — hermetic store + second adapter: the
            // swap proof is that IFeeService is the whole contract.
            services.AddSingleton(new FakeFeeRepository());
            services.AddSingleton<Lola.Application.Fees.IFeeRepository>(
                sp => sp.GetRequiredService<FakeFeeRepository>());
            services.AddSingleton<Lola.Application.Fees.IFeeService,
                AltStubFeeService>();
            // Documents (MPE-190) � hermetic registry + the
            // swap-proof second adapter.
            services.AddSingleton(new FakeDocumentRepository());
            services.AddSingleton<Lola.Application.Documents.IDocumentRepository>(
                sp => sp.GetRequiredService<FakeDocumentRepository>());
            services.AddSingleton<Lola.Application.Documents.IDocumentService,
                AltStubDocumentService>();
            // TRID tolerance (MPE-298) — hermetic mirror; the Dapper
            // impl is proven in Lola.DataPlane.Tests.
            services.AddSingleton(new FakeToleranceRepository());
            services.AddSingleton<Lola.Application.Compliance
                .IToleranceRepository>(
                sp => sp.GetRequiredService<FakeToleranceRepository>());
            services.AddSingleton(new FakeAtrQmRepository());
            services.AddSingleton<Lola.Application.Compliance
                .IAtrQmRepository>(
                sp => sp.GetRequiredService<FakeAtrQmRepository>());
            // Vendor seam (MPE-227) — hermetic registry + credential
            // source + second adapter: implementing IVendorAdapter is
            // the whole swap contract.
            services.AddSingleton(new FakeVendorOrderRepository());
            services.AddSingleton<Lola.Application.Vendors.IVendorOrderRepository>(
                sp => sp.GetRequiredService<FakeVendorOrderRepository>());
            services.AddSingleton(new AltStubVendorAdapter());
            services.AddSingleton<Lola.Application.Vendors.IVendorAdapter>(
                sp => sp.GetRequiredService<AltStubVendorAdapter>());
            services.AddSingleton(new FakeVendorCredentialSource());
            services.AddSingleton<Lola.Application.Vendors.IVendorCredentialSource>(
                sp => sp.GetRequiredService<FakeVendorCredentialSource>());
            // Validation feedback (M17) — hermetic ledger mirroring
            // the (source, feedback_id) dedupe semantics.
            services.AddSingleton(new FakeVendorFeedbackRepository());
            services.AddSingleton<Lola.Application.Vendors
                .IVendorFeedbackRepository>(
                sp => sp.GetRequiredService<
                    FakeVendorFeedbackRepository>());
            // Vendor-admin seam (MPE-191) — republish + erasure
            // revoke resolve it; the fake records calls hermetically.
            services.AddSingleton(new FakeDocVendorAdmin());
            services.AddSingleton<Lola.Application.Documents.IDocVendorAdmin>(
                sp => sp.GetRequiredService<FakeDocVendorAdmin>());
            // Document vault (MPE-200) — hermetic registry + loopback
            // field cipher: the real TransitFieldCipher would hit
            // OpenBao on every DEK wrap / filename encrypt. Object
            // vault + scanner stay the production stubs — the e2e
            // asserts quarantined bytes are ciphertext.
            services.AddSingleton(new FakeVaultDocRepository());
            services.AddSingleton<Lola.Application.Vault.IVaultDocRepository>(
                sp => sp.GetRequiredService<FakeVaultDocRepository>());
            services.AddSingleton<Lola.Application.Security.IFieldCipher,
                LoopbackFieldCipher>();
            services.AddSingleton<Lola.Application.Security.IBlindIndex>(
                _ => TestCrypto.Index);
            // BFF session refresh/revoke self-calls run through the
            // in-memory TestServer — a real socket to localhost would
            // miss the host entirely (MPE-289).
            services.AddHttpClient(
                    Lola.Api.Identity.BffSessionEndpoints.TokenClient)
                .ConfigurePrimaryHttpMessageHandler(_ => Server.CreateHandler());
            // Download-URL signer (MPE-202): hermetic HMAC instead of
            // transit — same verify contract, on a clock the test can
            // age for the expired-signature case.
            services.AddSingleton(new SettableTimeProvider());
            services.AddSingleton<Lola.Application.Vault.IVaultUrlSigner>(
                sp => new Lola.Infrastructure.Vault.StubVaultUrlSigner(
                    sp.GetRequiredService<SettableTimeProvider>()));
            // Small cap so the 413 path is exercisable without a
            // 25 MiB multipart body.
            services.AddSingleton(
                new Lola.Application.Vault.VaultUploadOptions(
                    128L << 10));
            // The request handler resolves the tenant's vendor
            // customer id off the registry row � the fake control
            // plane leaves it null, so provision a binding inline:
            // the real provisioning step is proven in DataPlane tests.
        });
    }

    /// Seed a tenant + active user into the control-plane DB the app
    /// under test is using (same singleton instance).
    public async Task<LolaUser> SeedUserAsync(
        string tenantId, string email, string password,
        LolaRole role = LolaRole.LoanOfficer,
        string? mfaSecretRef = "vault:dev-mfa-ref-1234",
        string tenantStatus = "active")
    {
        var db = Services.GetRequiredService<ControlPlaneDb>();
        // Crypto rides the APP's registrations — marquee factories swap
        // in the real transit cipher/index, so seeds must key off DI,
        // not the loopback statics.
        var cipher = Services.GetRequiredService<IFieldCipher>();
        var index = Services.GetRequiredService<IBlindIndex>();
        var registry = new TenantRegistry(db, cipher, index);
        await registry.InsertProvisioningAsync(
            tenantId, tenantId, $"Tenant {tenantId}", $"data/tenants/{tenantId}.db");
        await registry.SetStatusAsync(tenantId, tenantStatus);

        var hasher = new TestPasswordHasher();
        var user = new LolaUser(
            $"u-{Guid.NewGuid():N}", tenantId, email, "Test User",
            hasher.Hash(password), UserStatus.Active, MfaEnabled: false,
            "2026-10-27T00:00:00Z", mfaSecretRef);
        var users = new UserDirectory(db, cipher, index);
        await users.InsertAsync(user);

        // Group + role + membership so RolesForUserAsync resolves.
        // Name = gid: (tenant_id, name_idx) is UNIQUE and one tenant can
        // hold several seeded users across a test.
        var gid = $"g-{Guid.NewGuid():N}";
        string nameCt, nameIdx;
        using (CipherContext.Use(tenantId))
        {
            nameCt = await cipher.EncryptAsync(gid);
            nameIdx = await index.IndexAsync(
                Lola.Domain.Classification.PiiClass.Other, gid);
        }
        using var conn = db.Open();
        await conn.ExecuteAsync(
            "INSERT INTO groups (id, tenant_id, name, name_idx) " +
            "VALUES (@gid, @t, @n, @ni)",
            new { gid, t = tenantId, n = nameCt, ni = nameIdx });
        await conn.ExecuteAsync(
            "INSERT INTO group_roles (group_id, role) VALUES (@gid, @r)",
            new { gid, r = role.ToString() });
        await conn.ExecuteAsync(
            "INSERT INTO user_groups (user_id, group_id) VALUES (@uid, @gid)",
            new { uid = user.Id, gid });
        return user;
    }

    /// Provisioning generates a random svc-{tenant} secret nobody
    /// keeps; tests recreate the client with a known secret through
    /// the manager so tid-scoped service tokens can be minted.
    public async Task ResetServiceClientSecretAsync(string tenantId, string secret)
    {
        await using var scope = Services.CreateAsyncScope();
        var applications = scope.ServiceProvider
            .GetRequiredService<IOpenIddictApplicationManager>();
        var clientId = $"svc-{tenantId}";
        var client = await applications.FindByClientIdAsync(clientId)
            ?? throw new InvalidOperationException($"{clientId} not provisioned");
        await applications.DeleteAsync(client);
        await applications.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = secret,
            ClientType = ClientTypes.Confidential,
            DisplayName = $"Service account — {tenantId}",
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + "lola-api"
            }
        });
    }
}
