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

/// MPE-226 acceptance on a real SQLCipher tenant file: the enqueue
/// transaction writes order+outbox atomically, request_hash dedupes,
/// submitted/completed/failed transitions land, and the outbox drain
/// states (due → done | retry → dlq) behave like the fee precedent.
[Collection("openbao")]
public sealed class VendorRepositoryTests(OpenBaoFixture fixture)
{
    static VendorRepositoryTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private async Task<(VendorRepository repo, TenantRecord rec)>
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
        // vendor_orders FK → applications — seed a minimal row.
        using (TenantContext.Use(TenantScope.From(rec)))
        {
            await using var conn = await factory.OpenAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO applications (id, status, current_version)
                VALUES ('app-1', 'submitted', 1)
                """);
        }
        return (new VendorRepository(factory), rec);
    }

    private static VendorOrderRow Order(string id = "ord-1",
        string hash = "hash-1", string kind = "credit") => new(
        id, "app-1", kind, "stub", null, "ordered",
        "vault:v1:reqcipher", null, null, "officer-1",
        "2026-11-17T00:00:00Z", null, null, hash);

    [Fact]
    public async Task Enqueue_writes_order_and_outbox_atomically()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));

        var result = await repo.EnqueueAsync(Order(), "ob-1");

        result.ShouldBe(new VendorEnqueueResult("ord-1", false));
        var order = await repo.GetAsync("ord-1");
        order.ShouldNotBeNull();
        order.Status.ShouldBe("ordered");
        order.Kind.ShouldBe("credit");
        order.RequestJson.ShouldBe("vault:v1:reqcipher");
        (await repo.ListDueOutboxAsync("2099-01-01", 10))
            .Single().OrderId.ShouldBe("ord-1");
    }

    [Fact]
    public async Task Enqueue_dedupes_on_request_hash()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        await repo.EnqueueAsync(Order(), "ob-1");

        var replay = await repo.EnqueueAsync(
            Order(id: "ord-2"), "ob-2");

        replay.ShouldBe(new VendorEnqueueResult("ord-1", true));
        (await repo.ListByApplicationAsync("app-1")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Submitted_then_completed_transitions_the_order()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        await repo.EnqueueAsync(Order(), "ob-1");

        await repo.MarkSubmittedAsync("ord-1", "stub-credit-abc123", "ob-1");
        var submitted = await repo.GetAsync("ord-1");
        submitted!.Status.ShouldBe("submitted");
        submitted.ExternalId.ShouldBe("stub-credit-abc123");
        submitted.SubmittedUtc.ShouldNotBeNull();

        await repo.CompleteAsync(
            "ord-1", "app-1", "vault:v1:resultcipher", "ob-1",
            "completed", "Credit", "officer-1");
        var done = await repo.GetAsync("ord-1");
        done!.Status.ShouldBe("completed");
        done.ResultJson.ShouldBe("vault:v1:resultcipher");
        done.CompletedUtc.ShouldNotBeNull();
        (await repo.ListDueOutboxAsync("2099-01-01", 10))
            .ShouldBeEmpty();

        // External-id correlation — the webhook channel's lookup.
        (await repo.FindByExternalIdAsync(
            "credit", "stub-credit-abc123"))!.OrderId.ShouldBe("ord-1");
    }

    [Fact]
    public async Task Fail_marks_order_failed_and_closes_outbox()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        await repo.EnqueueAsync(Order(), "ob-1");

        await repo.FailAsync("ord-1", "app-1", "vendor-auth", "ob-1",
            1, "Credit", "officer-1");

        var order = await repo.GetAsync("ord-1");
        order!.Status.ShouldBe("failed");
        order.Error.ShouldBe("vendor-auth");
        (await repo.ListDueOutboxAsync("2099-01-01", 10))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Retry_and_dlq_transitions_follow_drain_policy()
    {
        var (repo, rec) = await ProvisionAsync();
        using var _ = TenantContext.Use(TenantScope.From(rec));
        await repo.EnqueueAsync(Order(), "ob-1");

        await repo.MarkOutboxRetryAsync(
            "ob-1", 1, "2026-11-17T00:00:05Z", "timeout");
        var row = (await repo.ListDueOutboxAsync(
            "2026-11-17T00:00:04Z", 10));
        row.ShouldBeEmpty(); // not due yet
        row = await repo.ListDueOutboxAsync("2026-11-17T00:00:06Z", 10);
        row.Single().Attempts.ShouldBe(1);
        row.Single().LastError.ShouldBe("timeout");

        await repo.MarkOutboxDlqAsync("ob-1", 5, "terminal");
        (await repo.ListDueOutboxAsync("2099-01-01", 10))
            .ShouldBeEmpty();
    }
}
