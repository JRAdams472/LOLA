using Dapper;
using Lola.Application.Events;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.DataPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// M15 P1 acceptance: migration 0014 applies on a fresh tenant file,
/// `loan_events` chains + tamper-detects like `audit_events`, and
/// `conditions` enforces its state machine.
[Collection("openbao")]
public sealed class LoanEventLogTests(OpenBaoFixture fixture)
{
    static LoanEventLogTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private (LoanEventLog events, ConditionRepository conditions,
             TenantProvisioningService svc, TenantRegistry registry,
             TransitFieldCipher cipher) Sut()
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
        var factory = new TenantConnectionFactory(cipher);
        return (new LoanEventLog(factory), new ConditionRepository(factory),
                svc, registry, cipher);
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

    private static LoanEventDraft Draft(string appId, string actor,
        string? after = "1000") => new(appId, "field_change", actor,
        "application", appId, "loan.amount", null, "900", after);

    [Fact]
    public async Task Loan_events_chain_verify_and_tamper_is_detected()
    {
        var (events, _, svc, registry, cipher) = Sut();
        var id = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
            var rec = (await registry.FindByIdAsync(id))!;
            var keyHex = await UnwrapAsync(rec, cipher);
            using (TenantContext.Use(TenantScope.From(rec)))
            {
                // Migration proof: both tables + indexes on the fresh file.
                using (var conn = KeyedSqliteConnectionManager.OpenKeyed(
                    rec.DbPath, keyHex))
                {
                    var objects = (await conn.QueryAsync<string>(
                        "SELECT name FROM sqlite_master WHERE name IN ("
                        + "'loan_events','conditions',"
                        + "'ix_loan_events_app','ix_loan_events_entity',"
                        + "'ix_conditions_app')"))
                        .ToList();
                    objects.ShouldContain("loan_events");
                    objects.ShouldContain("conditions");
                    objects.ShouldContain("ix_loan_events_app");
                    objects.ShouldContain("ix_loan_events_entity");
                    objects.ShouldContain("ix_conditions_app");
                }

                await events.WriteAsync(Draft("app-1", "officer-1"));
                await events.WriteAsync(Draft("app-1", "officer-1", "1100"));
                await events.WriteAsync(new LoanEventDraft("app-1",
                    "transition", "svc", "application", "app-1"));

                var ok = await events.VerifyChainAsync();
                ok.Ok.ShouldBeTrue();
                ok.EventCount.ShouldBe(3);

                var listed = await events.QueryAsync("app-1");
                listed.Count.ShouldBe(3);
                listed[0].EventKind.ShouldBe("transition"); // seq DESC
            }

            long brokenSeq;
            using (var conn = KeyedSqliteConnectionManager.OpenKeyed(
                rec.DbPath, keyHex))
            {
                brokenSeq = await conn.QuerySingleAsync<long>(
                    "SELECT seq FROM loan_events ORDER BY seq DESC LIMIT 1 OFFSET 1");
                await conn.ExecuteAsync(
                    "UPDATE loan_events SET after_val='tampered' WHERE seq=@s",
                    new { s = brokenSeq });
            }
            using (TenantContext.Use(TenantScope.From(rec)))
            {
                var bad = await events.VerifyChainAsync();
                bad.Ok.ShouldBeFalse();
                bad.FirstBrokenSeq.ShouldBe(brokenSeq);
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }

    [Fact]
    public async Task Conditions_lifecycle_enforces_state_machine()
    {
        var (_, conditions, svc, registry, cipher) = Sut();
        var id = fixture.NewTenant();
        try
        {
            await svc.ProvisionAsync(new ProvisionRequest(id, id, "T"));
            var rec = (await registry.FindByIdAsync(id))!;
            var keyHex = await UnwrapAsync(rec, cipher);
            using (TenantContext.Use(TenantScope.From(rec)))
            {
                var appId = $"app-{Guid.NewGuid():N}"[..16];
                using (var conn = KeyedSqliteConnectionManager.OpenKeyed(
                    rec.DbPath, keyHex))
                {
                    await conn.ExecuteAsync(
                        "INSERT INTO applications (id, status, borrower_user_id, current_version)"
                        + " VALUES (@id, 'draft', NULL, 0)",
                        new { id = appId });
                }

                var c = await conditions.OpenAsync(new ConditionDraft(
                    appId, "doc_required", "Bank statement needed",
                    Blocking: true, OpenedBy: "auto:classifier"));
                c.State.ShouldBe("open");
                c.Blocking.ShouldBeTrue();
                c.ConditionId.ShouldStartWith("cond-");

                var closed = await conditions.TransitionAsync(
                    c.ConditionId, "closed", "doc received");
                closed.State.ShouldBe("closed");
                closed.CloseReason.ShouldBe("doc received");

                var reopened = await conditions.TransitionAsync(
                    c.ConditionId, "open", null);
                reopened.State.ShouldBe("open");

                var waived = await conditions.TransitionAsync(
                    c.ConditionId, "waived", "not required for this product");
                waived.State.ShouldBe("waived");

                // Waived is terminal — any move out is illegal.
                await Should.ThrowAsync<Lola.Application.Errors.DomainException>(
                    () => conditions.TransitionAsync(
                        c.ConditionId, "open", null));

                var open = await conditions.ListByApplicationAsync(
                    appId, "open");
                open.ShouldBeEmpty();
                var all = await conditions.ListByApplicationAsync(appId);
                all.Count.ShouldBe(1);
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit).DestroyAsync(id);
        }
    }
}
