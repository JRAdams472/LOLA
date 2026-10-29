using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Domain.Applications;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-116 acceptance: tenant-file audit events append + hash-chain
/// verify end-to-end on real OpenBao + SQLCipher, and a tampered row
/// breaks the chain at exactly that seq.
[Collection("openbao")]
public sealed class TenantAuditLogTests(OpenBaoFixture fixture)
{
    static TenantAuditLogTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private (TenantAuditLog audit, TenantProvisioningService svc,
             TenantRegistry registry, TransitFieldCipher cipher) Sut()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"));
        db.Migrate();
        var registry = new TenantRegistry(db);
        var cipher = new TransitFieldCipher(fixture.Transit);
        var runner = new TenantMigrationRunner(
            cipher, registry, new TenantMigrationTracker(db));
        var svc = new TenantProvisioningService(
            registry, new TransitKeyManager(fixture.Transit),
            new TenantDbProvisioner(cipher), new StubServiceClients(),
            runner, fixture.DataDir);
        var audit = new TenantAuditLog(new TenantConnectionFactory(cipher));
        return (audit, svc, registry, cipher);
    }

    private static async Task<string> UnwrapAsync(
        TenantRecord tenant, TransitFieldCipher cipher)
    {
        using (CipherContext.Use(tenant.Id))
        {
            var b64 = await cipher.DecryptAsync(tenant.DbKeyWrapped!);
            return Convert.ToHexString(Convert.FromBase64String(b64));
        }
    }

    [Fact]
    public async Task Audit_events_chain_verify_and_tamper_is_detected()
    {
        var (audit, svc, registry, cipher) = Sut();
        var id = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
            var rec = (await registry.FindByIdAsync(id))!;
            var keyHex = await UnwrapAsync(rec, cipher);
            using (TenantContext.Use(TenantScope.From(rec)))
            {
                await audit.WriteAsync("officer-1", "read", "application",
                    "app-1", ["Ssn", "Name"], null);
                await audit.WriteAsync("officer-1", "write", "application",
                    "app-1", ["Ssn"], "draft patch");
                await audit.WriteAsync("officer-2", "search", "application",
                    null, ["Ssn"], "blind search");

                var ok = await audit.VerifyChainAsync();
                ok.Ok.ShouldBeTrue();
                ok.EventCount.ShouldBe(3);
                ok.FirstBrokenSeq.ShouldBeNull();
            }

            // Tamper: flip a row's field_classes via a raw keyed
            // connection — the chain must report exactly that seq.
            long brokenSeq;
            using (var conn = KeyedSqliteConnectionManager.OpenKeyed(rec.DbPath, keyHex))
            {
                brokenSeq = await conn.QuerySingleAsync<long>(
                    "SELECT seq FROM audit_events ORDER BY seq DESC LIMIT 1 OFFSET 1");
                await conn.ExecuteAsync(
                    "UPDATE audit_events SET field_classes='[\"Dob\"]' WHERE seq=@s",
                    new { s = brokenSeq });
            }
            using (TenantContext.Use(TenantScope.From(rec)))
            {
                var bad = await audit.VerifyChainAsync();
                bad.Ok.ShouldBeFalse();
                bad.FirstBrokenSeq.ShouldBe(brokenSeq);
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }
}
