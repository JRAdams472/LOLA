using Dapper;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// M26 P1 (MPE-321) migration acceptance on a real SQLCipher tenant
/// file: 0023_m26_credit_avm.sql rolls with the fleet — both tables
/// exist, accept inserts, carry the queryable late columns, and the
/// append-only shape round-trips.
[Collection("openbao")]
public sealed class CreditAvmMigrationTests(OpenBaoFixture fixture)
{
    static CreditAvmMigrationTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private async Task<(TenantConnectionFactory Factory,
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
        return (new TenantConnectionFactory(cipher), rec);
    }

    [Fact]
    public async Task Credit_reports_table_round_trips_queryable_columns()
    {
        var (factory, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        using var conn = await factory.OpenAsync(default);
        await conn.ExecuteAsync("""
            INSERT INTO credit_reports (
                id, application_id, order_id, bureaus_count,
                score_equifax, score_transunion, score_experian,
                score_model_equifax, score_model_transunion,
                score_model_experian,
                representative_score, representative_model,
                lates_30, lates_60, lates_90,
                merged_json, raw_json, report_hash,
                created_utc, created_by)
            VALUES (
                'cr-1', 'app-1', 'vo-1', 3,
                724, 731, 718,
                'fico-10t', 'vantagescore-4.0', 'fico-10t',
                724, 'fico-10t',
                2, 1, 1,
                '{"tradelines":[]}', '{"equifax":{}}', 'hash-1',
                '2026-11-15T00:00:00Z', 'officer-1')
            """);
        var row = await conn.QuerySingleAsync<(
            int? RepresentativeScore, int Lates30, int Lates60,
            int Lates90, string? RepresentativeModel)>(
            """
            SELECT representative_score AS RepresentativeScore,
                   lates_30 AS Lates30, lates_60 AS Lates60,
                   lates_90 AS Lates90,
                   representative_model AS RepresentativeModel
            FROM credit_reports WHERE application_id = 'app-1'
            """);
        row.RepresentativeScore.ShouldBe(724);
        row.Lates30.ShouldBe(2);
        row.Lates60.ShouldBe(1);
        row.Lates90.ShouldBe(1);
        row.RepresentativeModel.ShouldBe("fico-10t");
    }

    [Fact]
    public async Task Avm_results_table_round_trips()
    {
        var (factory, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        using var conn = await factory.OpenAsync(default);
        await conn.ExecuteAsync("""
            INSERT INTO avm_results (
                id, application_id, order_id, value, confidence, fsd,
                valuation_date, model, raw_json, result_hash,
                created_utc, created_by)
            VALUES (
                'avm-1', 'app-1', 'vo-2', 512000, 94, 0.06,
                '2026-11-14', 'synthetic-avm fixture-1.0', '{}', 'rh-1',
                '2026-11-15T00:00:00Z', 'officer-1')
            """);
        var row = await conn.QuerySingleAsync<(
            double Value, double Confidence, string? ValuationDate)>(
            """
            SELECT value AS Value, confidence AS Confidence,
                   valuation_date AS ValuationDate
            FROM avm_results WHERE application_id = 'app-1'
            """);
        row.Value.ShouldBe(512000);
        row.Confidence.ShouldBe(94);
        row.ValuationDate.ShouldBe("2026-11-14");
    }

    [Fact]
    public async Task Credit_reports_append_never_update_shape()
    {
        var (factory, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        using var conn = await factory.OpenAsync(default);
        foreach (var id in new[] { "cr-a", "cr-b" })
            await conn.ExecuteAsync("""
                INSERT INTO credit_reports (
                    id, application_id, bureaus_count,
                    lates_30, lates_60, lates_90,
                    merged_json, report_hash, created_utc, created_by)
                VALUES (
                    @id, 'app-1', 3, 0, 0, 0,
                    '{}', 'h', '2026-11-15T00:00:00Z', 'officer-1')
                """, new { id });
        var count = await conn.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM credit_reports WHERE application_id = 'app-1'");
        count.ShouldBe(2);
    }
}
