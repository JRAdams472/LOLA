using Dapper;
using Lola.Application.Identity;
using Lola.Application.Pricing;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-130 acceptance on a real SQLCipher tenant file: the enqueue
/// transaction writes eval+outbox atomically, request_hash dedupes,
/// and the worker transitions (due → done | retry → dlq) persist
/// exactly as the processor drives them.
[Collection("openbao")]
public sealed class PricingRepositoryTests(OpenBaoFixture fixture)
{
    static PricingRepositoryTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private async Task<(PricingRepository repo, TenantRecord rec,
        TenantProvisioningService svc)> ProvisionAsync()
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
        // pricing_evals FK → applications — seed a minimal row.
        using (TenantContext.Use(TenantScope.From(rec)))
        {
            await using var conn = await factory.OpenAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO applications (id, status, current_version)
                VALUES ('app-1', 'draft', 1)
                """);
        }
        return (new PricingRepository(factory), rec, svc);
    }

    private static PricingEvalRecord Eval(string id = "eval-1",
        string hash = "hash-1") => new(
        id, "app-1", "stub", null, hash, """{"loan":{}}""",
        "pending", null, null, "officer-1", null, null);

    [Fact]
    public async Task Enqueue_is_atomic_and_dedupes_by_request_hash()
    {
        var (repo, rec, _) = await ProvisionAsync();
        try
        {
            using (TenantContext.Use(TenantScope.From(rec)))
            {
                var first = await repo.EnqueueAsync(
                    Eval(), "ob-1", "eval", """{"s":1}""");
                first.Deduplicated.ShouldBeFalse();
                first.EvalId.ShouldBe("eval-1");

                var again = await repo.EnqueueAsync(
                    Eval("eval-2", "hash-1"), "ob-2", "eval",
                    """{"s":1}""");
                again.Deduplicated.ShouldBeTrue();
                again.EvalId.ShouldBe("eval-1"); // original, not eval-2

                var evals = await repo.ListEvalsAsync("app-1");
                evals.Count.ShouldBe(1); // nothing was inserted twice
                var due = await repo.ListDueOutboxAsync(
                    "2999-01-01T00:00:00.000Z", 10);
                due.Count.ShouldBe(1);
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit)
                .DestroyAsync(rec.Id);
        }
    }

    [Fact]
    public async Task Worker_cycle_due_complete_then_reschedule_dlq()
    {
        var (repo, rec, _) = await ProvisionAsync();
        try
        {
            using (TenantContext.Use(TenantScope.From(rec)))
            {
                // Happy path: due row completes eval+outbox atomically.
                await repo.EnqueueAsync(Eval(), "ob-1", "eval", "{}");
                var due = await repo.ListDueOutboxAsync(
                    "2999-01-01T00:00:00.000Z", 10);
                due.Single().EvalId.ShouldBe("eval-1");

                await repo.CompleteAsync("ob-1", "eval-1", "stub",
                    """{"products":[]}""", "2026-10-29T00:00:00.000Z");
                var eval = await repo.GetEvalAsync("eval-1");
                eval!.Status.ShouldBe("done");
                eval.ResultJson.ShouldBe("""{"products":[]}""");
                (await repo.ListDueOutboxAsync(
                    "2999-01-01T00:00:00.000Z", 10)).ShouldBeEmpty();

                // Retry path: reschedule hides the row until the backoff
                // instant passes; dead-letter fails both rows.
                await repo.EnqueueAsync(
                    Eval("eval-2", "hash-2"), "ob-2", "eval", "{}");
                await repo.RescheduleAsync("ob-2", "eval-2", 1,
                    "2999-01-01T00:00:00.000Z", "Unavailable: MPEx 503");
                (await repo.ListDueOutboxAsync(
                    "2026-01-01T00:00:00.000Z", 10)).ShouldBeEmpty();
                var later = await repo.ListDueOutboxAsync(
                    "2999-01-02T00:00:00.000Z", 10);
                later.Single().Attempts.ShouldBe(1);

                await repo.DeadLetterAsync("ob-2", "eval-2", 5,
                    "Unavailable: MPEx 503");
                var failed = await repo.GetEvalAsync("eval-2");
                failed!.Status.ShouldBe("failed");
                failed.Error.ShouldContain("MPEx 503");
                (await repo.ListDueOutboxAsync(
                    "2999-01-02T00:00:00.000Z", 10)).ShouldBeEmpty();

                // History is the append-only spine — both evals read
                // back oldest-first.
                var history = await repo.ListEvalsAsync("app-1");
                history.Select(e => e.EvalId)
                    .ShouldBe(["eval-1", "eval-2"]);
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit)
                .DestroyAsync(rec.Id);
        }
    }
}
