using Dapper;
using Lola.Application.Fees;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-183 acceptance on a real SQLCipher tenant file: the enqueue
/// transaction writes eval+outbox atomically, request_hash dedupes,
/// completion persists eval+outbox+fee_items in one tx, and the
/// worker transitions (due → done | retry → dlq) land exactly as
/// the processor drives them.
[Collection("openbao")]
public sealed class FeeRepositoryTests(OpenBaoFixture fixture)
{
    static FeeRepositoryTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private async Task<(FeeRepository repo, TenantRecord rec)>
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
        // fee_evals FK → applications — seed a minimal row.
        using (TenantContext.Use(TenantScope.From(rec)))
        {
            await using var conn = await factory.OpenAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO applications (id, status, current_version)
                VALUES ('app-1', 'draft', 1)
                """);
        }
        return (new FeeRepository(factory), rec);
    }

    private static FeeEvalRecord Eval(string id = "eval-1",
        string hash = "hash-1") => new(
        id, "app-1", "stub", hash, """{"loan":{}}""",
        "pending", null, null, "officer-1", null, null);

    private static IReadOnlyList<FeeItemRecord> Items(string evalId) =>
    [
        new FeeItemRecord("item-1", evalId, "origination",
            "Origination Fee", "A", 900m,
            """[{"rule":"base","op":"set","value":900,"runningTotal":900}]""",
            900m, null),
        new FeeItemRecord("item-2", evalId, "transfer-tax",
            "Transfer Tax", "E", 400m,
            """[{"rule":"rate","op":"mul","value":0.001,"runningTotal":400}]""",
            400m, null),
    ];

    [Fact]
    public async Task Enqueue_is_atomic_and_dedupes_by_request_hash()
    {
        var (repo, rec) = await ProvisionAsync();
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
    public async Task Complete_writes_eval_outbox_and_items_atomically()
    {
        var (repo, rec) = await ProvisionAsync();
        try
        {
            using (TenantContext.Use(TenantScope.From(rec)))
            {
                await repo.EnqueueAsync(Eval(), "ob-1", "eval", "{}");
                await repo.CompleteAsync("ob-1", "eval-1", "zara",
                    """{"fees":[]}""", Items("eval-1"),
                    "2026-10-29T00:00:00.000Z");

                var eval = await repo.GetEvalAsync("eval-1");
                eval!.Status.ShouldBe("done");
                eval.ResultJson.ShouldBe("""{"fees":[]}""");
                eval.CompletedUtc.ShouldBe(
                    "2026-10-29T00:00:00.000Z");
                (await repo.ListDueOutboxAsync(
                    "2999-01-01T00:00:00.000Z", 10)).ShouldBeEmpty();

                await using var conn = await new TenantConnectionFactory(
                    new TransitFieldCipher(fixture.Transit)).OpenAsync();
                var items = (await conn.QueryAsync<dynamic>(
                    """
                    SELECT fee_code, section, total FROM fee_items
                    WHERE eval_id='eval-1' ORDER BY item_id
                    """)).ToList();
                items.Count.ShouldBe(2);
                ((string)items[0].fee_code).ShouldBe("origination");
                ((string)items[0].section).ShouldBe("A");
                ((double)items[0].total).ShouldBe(900.0);
                ((string)items[1].fee_code).ShouldBe("transfer-tax");
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit)
                .DestroyAsync(rec.Id);
        }
    }

    [Fact]
    public async Task Worker_cycle_reschedule_then_dlq()
    {
        var (repo, rec) = await ProvisionAsync();
        try
        {
            using (TenantContext.Use(TenantScope.From(rec)))
            {
                await repo.EnqueueAsync(Eval(), "ob-1", "eval", "{}");
                // Reschedule hides the row until the backoff instant.
                await repo.RescheduleAsync("ob-1", "eval-1", 1,
                    "2999-01-01T00:00:00.000Z", "Unavailable: Zara 503");
                (await repo.ListDueOutboxAsync(
                    "2026-01-01T00:00:00.000Z", 10)).ShouldBeEmpty();
                var later = await repo.ListDueOutboxAsync(
                    "2999-01-02T00:00:00.000Z", 10);
                later.Single().Attempts.ShouldBe(1);

                await repo.DeadLetterAsync("ob-1", "eval-1", 5,
                    "Unavailable: Zara 503");
                var failed = await repo.GetEvalAsync("eval-1");
                failed!.Status.ShouldBe("failed");
                failed.Error.ShouldContain("Zara 503");
                (await repo.ListDueOutboxAsync(
                    "2999-01-02T00:00:00.000Z", 10)).ShouldBeEmpty();

                var history = await repo.ListEvalsAsync("app-1");
                history.Select(e => e.EvalId).ShouldBe(["eval-1"]);
            }
        }
        finally
        {
            await new TransitKeyManager(fixture.Transit)
                .DestroyAsync(rec.Id);
        }
    }
}
