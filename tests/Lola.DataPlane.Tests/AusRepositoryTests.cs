using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// M25 P2 (MPE-316) acceptance on a real SQLCipher tenant file:
/// AusRepository results/findings round-trip through real SQL —
/// append-only lineage, per-vendor queries, raw XML confined.
[Collection("openbao")]
public sealed class AusRepositoryTests(OpenBaoFixture fixture)
{
    static AusRepositoryTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private async Task<(AusRepository Repo,
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
        return (new AusRepository(new TenantConnectionFactory(cipher)),
            rec);
    }

    [Fact]
    public async Task Results_append_and_list_newest_first()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        var first = await repo.AddResultAsync("app-1", "du", "vo-1",
            "case-1", "approve-eligible", "approve", "<xml/>", "h1", 2,
            "officer-1", default);
        await Task.Delay(5);
        var second = await repo.AddResultAsync("app-1", "lpa", "vo-2",
            "key-1", "accept", "accept", "<xml2/>", "h2", 1,
            "officer-1", default);

        var rows = await repo.ListResultsAsync("app-1", default);
        rows.Count.ShouldBe(2);
        rows[0].ResultId.ShouldBe(second.ResultId);
        rows[1].ResultId.ShouldBe(first.ResultId);
        rows[0].Vendor.ShouldBe("lpa");
        rows[0].CaseId.ShouldBe("key-1");
        rows[0].FindingsCount.ShouldBe(1);
    }

    [Fact]
    public async Task Findings_round_trip_with_condition_linkage()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        var result = await repo.AddResultAsync("app-1", "du", null,
            null, "refer", null, null, "h", 2, "sys", default);
        await repo.AddFindingAsync(result.ResultId, "3893", "credit",
            "info", "credit note", null, default);
        await repo.AddFindingAsync(result.ResultId, "X1", "income",
            "required", "verify income", "cond-7", default);

        var findings = await repo.ListFindingsAsync(result.ResultId, default);
        findings.Count.ShouldBe(2);
        findings[0].VendorCode.ShouldBe("3893");
        findings[0].ConditionId.ShouldBeNull();
        findings[1].VendorCode.ShouldBe("X1");
        findings[1].ConditionId.ShouldBe("cond-7");
        findings[1].Severity.ShouldBe("required");
    }

    [Fact]
    public async Task Results_are_scoped_per_application()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        await repo.AddResultAsync("app-1", "du", null, null,
            "approve-eligible", null, null, "h", 0, "sys", default);
        await repo.AddResultAsync("app-2", "lpa", null, null,
            "caution", null, null, "h", 0, "sys", default);

        (await repo.ListResultsAsync("app-1", default))
            .ShouldHaveSingleItem().Vendor.ShouldBe("du");
        (await repo.ListResultsAsync("app-2", default))
            .ShouldHaveSingleItem().Vendor.ShouldBe("lpa");
    }

    [Fact]
    public async Task Null_response_xml_round_trips_as_null_not_crash()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        var row = await repo.AddResultAsync("app-1", "du", null, null,
            "unable-to-evaluate", null, null, "h", 0, "sys", default);
        row.Recommendation.ShouldBe("unable-to-evaluate");
        (await repo.ListResultsAsync("app-1", default))
            .ShouldHaveSingleItem().RiskClass.ShouldBeNull();
    }
}
