using Lola.Application.Compliance;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// M24 P2 (MPE-313) acceptance on a real SQLCipher tenant file:
/// HmdaRepository rows/edits/snapshots round-trip through real SQL —
/// append-only lineage, year queries, freeze shape.
[Collection("openbao")]
public sealed class HmdaRepositoryTests(OpenBaoFixture fixture)
{
    static HmdaRepositoryTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private async Task<(HmdaRepository Repo,
            Lola.Domain.Identity.TenantRecord Rec)> ProvisionAsync()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"), TestDbKeys.ControlFileKeyHex);
        db.Migrate();
        var registry = new TenantRegistry(db, TestCrypto.Cipher, TestCrypto.Index);
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
        return (new HmdaRepository(new TenantConnectionFactory(cipher)),
            rec);
    }

    private const string RowJson = """{"record_identifier":"2","uli":"X"}""";

    [Fact]
    public async Task Lar_rows_append_and_list_newest_first()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        var first = await repo.AddLarRowAsync("app-1", 2026,
            "ULI1", RowJson, "{}", "officer-1", default);
        await Task.Delay(5);
        var second = await repo.AddLarRowAsync("app-1", 2026,
            "ULI2", RowJson, "{}", "officer-1", default);

        var rows = await repo.ListLarRowsAsync("app-1", default);
        rows.Count.ShouldBe(2);
        rows[0].LarRowId.ShouldBe(second.LarRowId);
        rows[1].LarRowId.ShouldBe(first.LarRowId);
        rows[0].Uli.ShouldBe("ULI2");
        rows[0].RowJson.ShouldBe(RowJson);
        rows[0].Actor.ShouldBe("officer-1");
    }

    [Fact]
    public async Task Year_query_spans_applications()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        await repo.AddLarRowAsync("app-1", 2026, "U1", RowJson, "{}", "a", default);
        await repo.AddLarRowAsync("app-2", 2026, "U2", RowJson, "{}", "a", default);
        await repo.AddLarRowAsync("app-3", 2025, "U3", RowJson, "{}", "a", default);

        var y2026 = await repo.ListLarRowsByYearAsync(2026, default);
        y2026.Count.ShouldBe(2);
        var y2025 = await repo.ListLarRowsByYearAsync(2025, default);
        y2025.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Edit_runs_roundtrip_in_insertion_order()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        var row = await repo.AddLarRowAsync("app-1", 2026,
            "U1", RowJson, "{}", "a", default);
        await repo.AddEditRunAsync(row.LarRowId, "validity", "V608",
            "pass", "", default);
        await repo.AddEditRunAsync(row.LarRowId, "quality", "Q603",
            "warn", "rate above 25%", default);

        var runs = await repo.ListEditRunsAsync(row.LarRowId, default);
        runs.Count.ShouldBe(2);
        runs[0].EditId.ShouldBe("V608");
        runs[1].EditId.ShouldBe("Q603");
        runs[1].Result.ShouldBe("warn");
        runs[1].Detail.ShouldBe("rate above 25%");
    }

    [Fact]
    public async Task Snapshot_freeze_appends_with_row_mappings()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        var r1 = await repo.AddLarRowAsync("app-1", 2026,
            "U1", RowJson, "{}", "a", default);
        var r2 = await repo.AddLarRowAsync("app-2", 2026,
            "U2", RowJson, "{}", "a", default);

        var snap = await repo.AddSnapshotAsync(2026,
            [r1.LarRowId, r2.LarRowId], "officer-1", default);
        snap.RowCount.ShouldBe(2);
        snap.ReportingYear.ShouldBe(2026);

        // re-freeze appends a NEW snapshot — original never rewrites
        var snap2 = await repo.AddSnapshotAsync(2026,
            [r1.LarRowId, r2.LarRowId], "officer-1", default);
        snap2.SnapshotId.ShouldNotBe(snap.SnapshotId);

        var all = await repo.ListSnapshotsAsync(default);
        all.Count.ShouldBe(2);
    }
}
