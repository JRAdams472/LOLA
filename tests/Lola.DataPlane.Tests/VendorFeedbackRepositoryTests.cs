using Dapper;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Application.Vendors;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-257 acceptance on a real SQLCipher tenant file: the
/// vendor_feedback ledger migrates, TryRecord dedupes on
/// (source, feedback_id), and the composite key lets two sources
/// share a feedback_id without colliding.
[Collection("openbao")]
public sealed class VendorFeedbackRepositoryTests(OpenBaoFixture fixture)
{
    static VendorFeedbackRepositoryTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private async Task<(VendorFeedbackRepository repo, TenantRecord rec)>
        ProvisionAsync()
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
        // vendor_feedback FK → applications — seed a minimal row.
        using (TenantContext.Use(TenantScope.From(rec)))
        {
            await using var conn = await factory.OpenAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO applications (id, status, current_version)
                VALUES ('app-1', 'submitted', 1)
                """);
        }
        return (new VendorFeedbackRepository(factory), rec);
    }

    private static VendorFeedbackReceipt Receipt(
        string source = "mpex", string feedbackId = "vf-1",
        string finding = "cleared") => new(
        source, "app-1", feedbackId, finding, "info",
        "condition", "cond-1");

    [Fact]
    public async Task TryRecord_persists_the_receipt()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));

        (await repo.TryRecordAsync(Receipt())).ShouldBeTrue();

        var row = await repo.GetAsync("mpex", "vf-1");
        row.ShouldNotBeNull();
        row.Source.ShouldBe("mpex");
        row.Finding.ShouldBe("cleared");
        row.LinkedEntityId.ShouldBe("cond-1");
        row.ReceivedUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task TryRecord_replay_is_a_recorded_noop()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        await repo.TryRecordAsync(Receipt());

        // Same key, different payload — the ledger keeps the first.
        var replay = await repo.TryRecordAsync(
            Receipt(finding: "exception"));

        replay.ShouldBeFalse();
        var rows = await repo.ListByApplicationAsync("app-1");
        rows.ShouldHaveSingleItem().Finding.ShouldBe("cleared");
    }

    [Fact]
    public async Task TryRecord_same_feedback_id_across_sources_is_not_a_replay()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));

        (await repo.TryRecordAsync(Receipt(source: "mpex")))
            .ShouldBeTrue();
        (await repo.TryRecordAsync(Receipt(source: "isppr")))
            .ShouldBeTrue();

        (await repo.ListByApplicationAsync("app-1")).Count.ShouldBe(2);
    }
}
