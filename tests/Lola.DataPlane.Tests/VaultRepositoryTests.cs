using Dapper;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-200 acceptance on a real SQLCipher tenant file: enqueue writes
/// doc+outbox atomically, request_hash dedupes identical uploads,
/// `filename` and `wrapped_dek` are `vault:` ciphertext at rest
/// (PII-class — invariant 1), reads decrypt, and the scan lifecycle
/// transitions land atomically.
[Collection("openbao")]
public sealed class VaultRepositoryTests(OpenBaoFixture fixture)
{
    static VaultRepositoryTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private async Task<(VaultRepository repo, TenantRecord rec,
        TenantConnectionFactory factory)> ProvisionAsync()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"));
        db.Migrate();
        var registry = new TenantRegistry(db);
        var cipher = new TransitFieldCipher(fixture.Transit);
        var svc = new TenantProvisioningService(
            registry, new TransitKeyManager(fixture.Transit),
            new TenantDbProvisioner(cipher), new StubServiceClients(),
            new TenantMigrationRunner(cipher, registry,
                new TenantMigrationTracker(db)),
            fixture.DataDir);
        var id = fixture.NewTenant();
        await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
        var rec = (await registry.FindByIdAsync(id))!;
        var factory = new TenantConnectionFactory(cipher);
        using (TenantContext.Use(TenantScope.From(rec)))
        using (Lola.Application.Security.CipherContext.Use(id))
        {
            await using var conn = await factory.OpenAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO applications (id, status, current_version)
                VALUES ('app-1', 'draft', 1)
                """);
        }
        return (new VaultRepository(factory, cipher), rec, factory);
    }

    private static VaultDocRecord Doc(string id = "vlt-1",
        string hash = "up:aa:app-1:paystub") => new(
        id, "app-1", "paystub", "paystub-march.pdf", "application/pdf",
        12_345, "ab12", "vault:v1:wrapped-dek", "stub",
        $"quarantine/{id}", "quarantined", null, "officer-1",
        "", null, hash);

    [Fact]
    public async Task Enqueue_is_atomic_dedupes_and_encrypts_filename()
    {
        var (repo, rec, factory) = await ProvisionAsync();
        using var _t = TenantContext.Use(TenantScope.From(rec));
        using var _c = Lola.Application.Security.CipherContext.Use(rec.Id);

        var first = await repo.EnqueueAsync(Doc(), "vso-1");
        first.Deduplicated.ShouldBeFalse();

        // Raw read: filename + wrapped_dek are ciphertext at rest.
        await using var conn = await factory.OpenAsync();
        var raw = await conn.QuerySingleAsync<(string Filename,
            string Dek)>(
            "SELECT filename AS Filename, wrapped_dek AS Dek " +
            "FROM vault_docs WHERE vault_doc_id='vlt-1'");
        raw.Filename.ShouldStartWith("vault:");
        raw.Dek.ShouldStartWith("vault:");
        var outbox = await conn.QuerySingleAsync<string>(
            "SELECT status FROM vault_scan_outbox " +
            "WHERE vault_doc_id='vlt-1'");
        outbox.ShouldBe("pending");

        // Same bytes+app+classification → same row, no duplicate.
        var second = await repo.EnqueueAsync(
            Doc(id: "vlt-2"), "vso-2");
        second.Deduplicated.ShouldBeTrue();
        second.VaultDocId.ShouldBe("vlt-1");

        var got = await repo.GetAsync("vlt-1");
        got!.Filename.ShouldBe("paystub-march.pdf");
        got.Classification.ShouldBe("paystub");
        got.S3Key.ShouldBe("quarantine/vlt-1");
    }

    [Fact]
    public async Task Scan_lifecycle_transitions_land_atomically()
    {
        var (repo, rec, _) = await ProvisionAsync();
        using var _t = TenantContext.Use(TenantScope.From(rec));
        using var _c = Lola.Application.Security.CipherContext.Use(rec.Id);

        await repo.EnqueueAsync(Doc("vlt-clean", "up:cc:app-1:paystub"),
            "vso-c");
        await repo.EnqueueAsync(Doc("vlt-bad", "up:bb:app-1:id"),
            "vso-b");
        await repo.EnqueueAsync(Doc("vlt-down", "up:dd:app-1:w2"),
            "vso-d");

        var due = await repo.ListDueScansAsync(
            "2999-01-01T00:00:00.000Z", 10);
        due.Count.ShouldBe(3);

        // clean → done + s3_key promoted + scanned stamp.
        await repo.MarkScanningAsync("vso-c", "2999-02-01T00:00:00.000Z");
        await repo.CompleteScanAsync("vso-c", "vlt-clean",
            "clean/vlt-clean", "2999-01-02T00:00:00.000Z");
        var clean = await repo.GetAsync("vlt-clean");
        clean!.Status.ShouldBe("clean");
        clean.S3Key.ShouldBe("clean/vlt-clean");
        clean.ScannedUtc.ShouldBe("2999-01-02T00:00:00.000Z");

        // infected → done + signature recorded.
        await repo.MarkInfectedAsync("vso-b", "vlt-bad",
            "Eicar-Test-Signature", "2999-01-02T00:00:01.000Z");
        var bad = await repo.GetAsync("vlt-bad");
        bad!.Status.ShouldBe("infected");
        bad.Error.ShouldBe("Eicar-Test-Signature");

        // unavailable → outbox dlq + doc unavailable.
        await repo.MarkUnavailableAsync("vso-d", "vlt-down",
            "clamav unreachable");
        var down = await repo.GetAsync("vlt-down");
        down!.Status.ShouldBe("unavailable");

        // Nothing left due — done + dlq rows are terminal.
        (await repo.ListDueScansAsync("2999-01-01T00:00:00.000Z", 10))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task List_is_newest_first_and_app_scoped()
    {
        var (repo, rec, _) = await ProvisionAsync();
        using var _t = TenantContext.Use(TenantScope.From(rec));
        using var _c = Lola.Application.Security.CipherContext.Use(rec.Id);

        await repo.EnqueueAsync(Doc("vlt-a", "up:1:app-1:paystub"), "o1");
        await repo.EnqueueAsync(Doc("vlt-b", "up:2:app-1:w2"), "o2");
        await repo.EnqueueAsync(Doc("vlt-c", "up:3:app-1:id"), "o3");

        var list = await repo.ListAsync("app-1");
        list.Select(d => d.VaultDocId)
            .ShouldBe(["vlt-c", "vlt-b", "vlt-a"]);
        list.All(d => d.ApplicationId == "app-1").ShouldBeTrue();
    }
}
