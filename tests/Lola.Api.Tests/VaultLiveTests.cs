using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Util;
using Dapper;
using Lola.Application.Audit;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Lola.Infrastructure.OpenBao;
using Lola.Infrastructure.Vault;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SQLitePCL;

namespace Lola.Api.Tests;

/// MPE-201 live acceptance (never-skip): the real MinIO + ClamAV +
/// OpenBao + SQLCipher pipeline. Uploads are STAGED through the vault
/// path (seal → PutAsync → EnqueueAsync) — not the endpoint, whose
/// allowlist would 415 a text-file EICAR — then drained by the real
/// VaultScanProcessor. Proves: MinIO-held bytes are LVL1 ciphertext,
/// clean promotes quarantine/→clean/, EICAR lands `infected` and
/// never reaches clean/, ClamAV-down retries then dead-letters, and
/// provisioning creates + purges the tenant bucket.
///
///   OPENBAO_ADDR/OPENBAO_TOKEN  real transit (DEK wrap + file key)
///   S3_ENDPOINT/S3_ACCESS_KEY/S3_SECRET_KEY   real MinIO (:9200 dev)
///   CLAMAV_ADDR               real clamd (:3310 dev)
public sealed class VaultLiveTests
{
    static VaultLiveTests() =>
        raw.SetProvider(new SQLite3Provider_e_sqlcipher());

    private const string Eicar =
        "X5O!P%@AP[4\\PZX54(P^)7CC)7}$" +
        "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    private static string Require(string key) =>
        Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException(
            $"{key} is required — these tests prove the live " +
            "MinIO+ClamAV+OpenBao vault pipeline and never skip.");

    /// Everything a test needs: transit, S3, clam, control plane.
    private sealed class Stack : IDisposable
    {
        public required OpenBaoTransitClient Transit { get; init; }
        public required IAmazonS3 S3 { get; init; }
        public required ClamAvScanner Scanner { get; init; }
        public required TransitFieldCipher Cipher { get; init; }
        public required TenantConnectionFactory ConnFactory { get; init; }
        public required ControlPlaneDb Db { get; init; }
        public required TenantRegistry Registry { get; init; }
        public required string DataDir { get; init; }
        public void Dispose() => S3.Dispose();
    }

    private static async Task<Stack> StackAsync()
    {
        var transit = new OpenBaoTransitClient(new HttpClient(),
            Options.Create(new OpenBaoOptions
            {
                Address = Require("OPENBAO_ADDR"),
                Token = Require("OPENBAO_TOKEN")
            }));
        await transit.EnsureMountAsync();
        var s3 = new AmazonS3Client(
            new BasicAWSCredentials(
                Require("S3_ACCESS_KEY"), Require("S3_SECRET_KEY")),
            new AmazonS3Config
            {
                ServiceURL = Require("S3_ENDPOINT"),
                ForcePathStyle = true,
                RequestChecksumCalculation =
                    RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation =
                    ResponseChecksumValidation.WHEN_REQUIRED
            });
        var cipher = new TransitFieldCipher(transit);
        var dir = Path.Combine(Path.GetTempPath(), "lola-vault-live",
            Guid.NewGuid().ToString("N"));
        var db = new ControlPlaneDb(Path.Combine(dir, "control.db"));
        db.Migrate();
        return new Stack
        {
            Transit = transit,
            S3 = s3,
            Scanner = ClamAvScanner.Parse(Require("CLAMAV_ADDR")),
            Cipher = cipher,
            ConnFactory = new TenantConnectionFactory(cipher),
            Db = db,
            Registry = new TenantRegistry(db),
            DataDir = dir
        };
    }

    private sealed class NoopServiceClients : IServiceClientProvisioner
    {
        public Task<ServiceClientResult> EnsureAsync(string tenantId,
            CancellationToken ct = default)
            => Task.FromResult(
                new ServiceClientResult($"svc-{tenantId}", null));
    }

    /// Real provisioning including the vault step — the bucket must
    /// exist in MinIO after this runs.
    private static async Task<TenantRecord> ProvisionAsync(Stack s)
    {
        var svc = new TenantProvisioningService(
            s.Registry, new TransitKeyManager(s.Transit),
            new TenantDbProvisioner(s.Cipher), new NoopServiceClients(),
            new TenantMigrationRunner(s.Cipher, s.Registry,
                new TenantMigrationTracker(s.Db)),
            s.DataDir,
            vaultProvisioner: new TenantVaultProvisioner(s.S3));
        var id = $"vl{Guid.NewGuid():N}"[..12];
        var result = await svc.ProvisionAsync(
            new ProvisionRequest(id, id, "V"));
        result.StepsCompleted.ShouldContain("vault-bucket");
        var rec = (await s.Registry.FindByIdAsync(id))!;
        // Seed the applications row the vault_docs FK needs — the
        // endpoint path would create it through draft POST.
        using (TenantContext.Use(TenantScope.From(rec)))
        {
            await using var conn = await s.ConnFactory.OpenAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO applications (id, status, current_version)
                VALUES ('app-1', 'draft', 1)
                """);
        }
        return rec;
    }

    /// A processor bound to the real services — the same registrations
    /// Program.cs makes under VAULT_ADAPTER=minio.
    private static Lola.Api.Vault.VaultScanProcessor Processor(Stack s,
        IVirusScanner? scanner = null)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<IFieldCipher>(s.Cipher);
        sc.AddSingleton(s.ConnFactory);
        sc.AddScoped<IVaultDocRepository, VaultRepository>();
        sc.AddScoped<ITenantAuditLog, TenantAuditLog>();
        sc.AddSingleton<IObjectVault>(new MinioObjectVault(s.S3));
        sc.AddSingleton<IVirusScanner>(scanner ?? s.Scanner);
        sc.AddSingleton<VaultCipher>();
        return new Lola.Api.Vault.VaultScanProcessor(sc.BuildServiceProvider(),
            s.Registry, TimeProvider.System);
    }

    /// Stage an upload exactly as UploadVaultDoc leaves it: sealed
    /// ciphertext in quarantine/, registry row + pending scan row.
    private static async Task<string> StageAsync(Stack s,
        TenantRecord rec, byte[] plaintext, string hashSuffix)
    {
        var store = new MinioObjectVault(s.S3);
        var repo = new VaultRepository(s.ConnFactory, s.Cipher);
        var cipher = new VaultCipher(s.Cipher);
        using var _t = TenantContext.Use(TenantScope.From(rec));
        using var _c = CipherContext.Use(rec.Id);
        var docId = $"vlt-{Guid.NewGuid():N}";
        await using var sealing = cipher.Seal(
            new MemoryStream(plaintext), 25L << 20);
        await store.PutAsync($"quarantine/{docId}", sealing);
        var record = new VaultDocRecord(docId, "app-1", "paystub",
            "doc.pdf", "application/pdf", sealing.PlaintextLength,
            sealing.Sha256Hex, sealing.WrappedDek, "minio",
            $"quarantine/{docId}", "quarantined", null, "officer-1",
            "", null,
            $"up:{sealing.Sha256Hex}:app-1:{hashSuffix}");
        var enqueued = await repo.EnqueueAsync(
            record, $"vso-{Guid.NewGuid():N}");
        enqueued.Deduplicated.ShouldBeFalse();
        return docId;
    }

    private static async Task<VaultDocRecord> DocAsync(Stack s,
        TenantRecord rec, string docId)
    {
        using var _t = TenantContext.Use(TenantScope.From(rec));
        using var _c = CipherContext.Use(rec.Id);
        return (await new VaultRepository(s.ConnFactory, s.Cipher)
            .GetAsync(docId))!;
    }

    private static async Task<byte[]> MinioBytes(Stack s,
        TenantRecord rec, string key)
    {
        using var _t = TenantContext.Use(TenantScope.From(rec));
        await using var stream =
            await new MinioObjectVault(s.S3).GetAsync(key);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static async Task<bool> ObjectExists(Stack s,
        TenantRecord rec, string key)
    {
        using var _t = TenantContext.Use(TenantScope.From(rec));
        try
        {
            await new MinioObjectVault(s.S3).GetAsync(key);
            return true;
        }
        catch (Amazon.S3.AmazonS3Exception ex)
            when (ex.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<AuditEvent>> AuditAsync(
        Stack s, TenantRecord rec)
    {
        using var _t = TenantContext.Use(TenantScope.From(rec));
        using var _c = CipherContext.Use(rec.Id);
        return await new TenantAuditLog(s.ConnFactory)
            .QueryAsync(new AuditFilter());
    }

    [Fact]
    public async Task Provisioning_creates_and_reentry_keeps_bucket()
    {
        var s = await StackAsync();
        var rec = await ProvisionAsync(s);
        var bucket = $"lola-vault-{rec.Id}";
        (await AmazonS3Util.DoesS3BucketExistV2Async(s.S3, bucket))
            .ShouldBeTrue();
        // Re-entrant — a second EnsureAsync is a no-op, not a 409.
        await new TenantVaultProvisioner(s.S3).EnsureAsync(rec);
        (await AmazonS3Util.DoesS3BucketExistV2Async(s.S3, bucket))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task Clean_upload_promotes_and_round_trips()
    {
        var s = await StackAsync();
        var rec = await ProvisionAsync(s);
        var plaintext = System.Text.Encoding.ASCII.GetBytes(
            $"%PDF-1.4 marker-{Guid.NewGuid():N} %%EOF");
        var docId = await StageAsync(s, rec, plaintext, "clean");

        // The object MinIO holds is LVL1 ciphertext — the plaintext
        // marker is nowhere inside it (invariant 1).
        var held = await MinioBytes(s, rec, $"quarantine/{docId}");
        held[..4].ShouldBe("LVL1"u8.ToArray());
        held.AsSpan().IndexOf(plaintext).ShouldBe(-1);

        (await Processor(s).DrainTenantAsync(rec)).ShouldBe(1);

        var doc = await DocAsync(s, rec, docId);
        doc.Status.ShouldBe("clean");
        doc.S3Key.ShouldBe($"clean/{docId}");
        doc.ScannedUtc.ShouldNotBeNull();

        // Promoted, not duplicated — clean/ holds it, quarantine is
        // empty, and opening the clean object restores the bytes.
        (await ObjectExists(s, rec, $"clean/{docId}")).ShouldBeTrue();
        (await ObjectExists(s, rec, $"quarantine/{docId}"))
            .ShouldBeFalse();
        using var _t = TenantContext.Use(TenantScope.From(rec));
        using var _c = CipherContext.Use(rec.Id);
        await using var opened = await new VaultCipher(s.Cipher)
            .OpenAsync(doc.WrappedDek,
                await new MinioObjectVault(s.S3).GetAsync(doc.S3Key));
        using var ms = new MemoryStream();
        await opened.CopyToAsync(ms);
        ms.ToArray().ShouldBe(plaintext);

        (await AuditAsync(s, rec))
            .Select(e => e.Action).ShouldContain("vault_scan_clean");
    }

    [Fact]
    public async Task Eicar_is_infected_and_never_reaches_clean()
    {
        var s = await StackAsync();
        var rec = await ProvisionAsync(s);
        var docId = await StageAsync(s, rec,
            System.Text.Encoding.ASCII.GetBytes(Eicar), "eicar");

        (await Processor(s).DrainTenantAsync(rec)).ShouldBe(1);

        var doc = await DocAsync(s, rec, docId);
        doc.Status.ShouldBe("infected");
        doc.Error.ShouldNotBeNullOrEmpty();

        // Nothing under clean/, and the quarantined ciphertext is
        // destroyed — infected content never persists in the vault.
        (await ObjectExists(s, rec, $"clean/{docId}")).ShouldBeFalse();
        (await ObjectExists(s, rec, $"quarantine/{docId}"))
            .ShouldBeFalse();

        (await AuditAsync(s, rec))
            .Select(e => e.Action).ShouldContain("vault_scan_infected");
    }

    [Fact]
    public async Task Clamav_down_retries_then_dead_letters()
    {
        var s = await StackAsync();
        var rec = await ProvisionAsync(s);
        var docId = await StageAsync(s, rec,
            System.Text.Encoding.ASCII.GetBytes("harmless"), "down");
        // Port 9 (discard) refuses fast — same verdict as a dead clamd.
        var dead = new ClamAvScanner("localhost", 9);

        (await Processor(s, dead).DrainTenantAsync(rec)).ShouldBe(1);
        var doc = await DocAsync(s, rec, docId);
        doc.Status.ShouldBe("quarantined");   // retried, not failed

        // Outbox carries the retry budget — one execution spent, a
        // future retry time, and the unreachable error recorded.
        using var _t = TenantContext.Use(TenantScope.From(rec));
        using var _c = CipherContext.Use(rec.Id);
        var repo = new VaultRepository(s.ConnFactory, s.Cipher);
        var rows = await repo.ListDueScansAsync(
            "2999-01-01T00:00:00.000Z", 10);
        var row = rows.Single(r => r.VaultDocId == docId);
        row.Status.ShouldBe("pending");
        row.Attempts.ShouldBe(1);
        row.LastError.ShouldNotBeNullOrEmpty();

        // Force the budget to its last execution — the next drain
        // dead-letters instead of retrying forever.
        await repo.RescheduleScanAsync(row.OutboxId, 9,
            "2000-01-01T00:00:00.000Z", "forced");
        (await Processor(s, dead).DrainTenantAsync(rec)).ShouldBe(1);
        doc = await DocAsync(s, rec, docId);
        doc.Status.ShouldBe("unavailable");
        doc.Error.ShouldNotBeNullOrEmpty();
        (await AuditAsync(s, rec))
            .Select(e => e.Action)
            .ShouldContain("vault_scan_unavailable");
    }

    [Fact]
    public async Task Signed_url_verifies_under_real_transit()
    {
        var s = await StackAsync();
        var rec = await ProvisionAsync(s);
        var signer = new TransitVaultUrlSigner(s.Transit,
            TimeProvider.System);
        var exp = DateTimeOffset.UtcNow.AddMinutes(15);
        string sig;

        // Tenant-scoped sign+verify — the scope lives only inside
        // these blocks so it can never leak into provisioning.
        using (TenantContext.Use(TenantScope.From(rec)))
        {
            sig = await signer.SignAsync("doc-1", exp);
            sig.ShouldStartWith("vault:v");
            (await signer.VerifyAsync("doc-1", exp, sig)).ShouldBeTrue();
            (await signer.VerifyAsync("doc-1", exp,
                sig[..^2] + "AA")).ShouldBeFalse();      // tampered
            (await signer.VerifyAsync("doc-2", exp, sig))
                .ShouldBeFalse();                        // rebound doc
            (await signer.VerifyAsync("doc-1",
                exp.AddMinutes(1), sig)).ShouldBeFalse();// rebound exp
            (await signer.VerifyAsync("doc-1",
                DateTimeOffset.UtcNow.AddMinutes(-1), sig))
                .ShouldBeFalse();                        // expired
        }

        // Cross-tenant replay — a different tenant's index key HMACs
        // the identical payload differently. Only the transit key is
        // needed: the signature lives or dies with it either way.
        var otherId = $"vl{Guid.NewGuid():N}"[..12];
        await new TransitKeyManager(s.Transit)
            .EnsureKeysAsync(otherId);
        using (TenantContext.Use(new TenantScope(otherId, "", null)))
            (await signer.VerifyAsync("doc-1", exp, sig))
                .ShouldBeFalse();
    }

    [Fact]
    public async Task Purge_destroys_objects_and_bucket()
    {
        var s = await StackAsync();
        var rec = await ProvisionAsync(s);
        await StageAsync(s, rec,
            System.Text.Encoding.ASCII.GetBytes("purge me"), "purge");
        var bucket = $"lola-vault-{rec.Id}";

        using var _t = TenantContext.Use(TenantScope.From(rec));
        await new MinioObjectVault(s.S3).PurgeTenantAsync();
        (await AmazonS3Util.DoesS3BucketExistV2Async(s.S3, bucket))
            .ShouldBeFalse();
        // Idempotent — purging a missing bucket is a no-op.
        await new TenantVaultProvisioner(s.S3).PurgeAsync(rec);
    }
}
