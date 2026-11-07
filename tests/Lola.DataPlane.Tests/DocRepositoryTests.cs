using Dapper;
using Lola.Application.Documents;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-190 acceptance on a real SQLCipher tenant file: the enqueue
/// transaction writes doc+outbox atomically, request_hash dedupes,
/// payload_json is `vault:` ciphertext at rest (template-required
/// PII — invariant 1), the due-list read joins the vendor request
/// id AND decrypts the payload, and generate→poll→complete and
/// reschedule→dlq transitions land as the processor drives them.
[Collection("openbao")]
public sealed class DocRepositoryTests(OpenBaoFixture fixture)
{
    static DocRepositoryTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private async Task<(DocRepository repo, TenantRecord rec,
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
        return (new DocRepository(factory, cipher), rec, factory);
    }

    private static DocRecord Doc(string id = "doc-1",
        string hash = "hash-1") => new(
        id, "app-1", "le-package", "le-package-app-1", "stub",
        null, null, "pending", null, "officer-1", null, null, hash);

    private const string Payload =
        """
        {"customerId":"cust-1","documents":[{"payload":{"borrowerNames":["Ada Lovelace"]}}]}
        """;

    [Fact]
    public async Task Enqueue_is_atomic_dedupes_and_encrypts_payload()
    {
        var (repo, rec, factory) = await ProvisionAsync();
        using (TenantContext.Use(TenantScope.From(rec)))
        using (Lola.Application.Security.CipherContext.Use(rec.Id))
        {
            var first = await repo.EnqueueAsync(Doc(), "ob-1",
                "generate", Payload);
            first.Deduplicated.ShouldBeFalse();
            var second = await repo.EnqueueAsync(Doc(), "ob-2",
                "generate", Payload);
            second.Deduplicated.ShouldBeTrue();
            second.DocId.ShouldBe("doc-1");

            // Ciphertext at rest — never the disclosure plaintext.
            await using var conn = await factory.OpenAsync();
            var stored = await conn.QuerySingleAsync<string>(
                "SELECT payload_json FROM doc_outbox WHERE outbox_id='ob-1'");
            stored.ShouldStartWith("vault:");
            stored.ShouldNotContain("Lovelace");
        }
    }

    [Fact]
    public async Task Due_rows_join_request_id_and_decrypt_payload()
    {
        var (repo, rec, _) = await ProvisionAsync();
        using (TenantContext.Use(TenantScope.From(rec)))
        using (Lola.Application.Security.CipherContext.Use(rec.Id))
        {
            await repo.EnqueueAsync(Doc(), "ob-1", "generate", Payload);
            var due = await repo.ListDueOutboxAsync(
                "2999-01-01T00:00:00Z", 10);
            due.Count.ShouldBe(1);
            due[0].RequestId.ShouldBeNull(); // not yet submitted
            due[0].PayloadJson.ShouldBe(Payload); // decrypted on read

            await repo.MarkGeneratingAsync("ob-1", "doc-1", "req-1",
                "2020-01-01T00:00:00Z");
            var doc = await repo.GetDocAsync("doc-1");
            doc!.Status.ShouldBe("generating");
            doc.RequestId.ShouldBe("req-1");

            var repoll = await repo.ListDueOutboxAsync(
                "2999-01-01T00:00:00Z", 10);
            repoll.Count.ShouldBe(1);
            repoll[0].RequestId.ShouldBe("req-1");
        }
    }

    [Fact]
    public async Task Complete_and_dlq_transitions_persist()
    {
        var (repo, rec, _) = await ProvisionAsync();
        using (TenantContext.Use(TenantScope.From(rec)))
        using (Lola.Application.Security.CipherContext.Use(rec.Id))
        {
            await repo.EnqueueAsync(Doc(), "ob-1", "generate", Payload);
            await repo.CompleteAsync("ob-1", "doc-1", "req-1",
                "le-package-app-1_req-1.zip", "2026-01-01T00:00:00Z");
            var done = await repo.GetDocAsync("doc-1");
            done!.Status.ShouldBe("completed");
            done.Filename.ShouldBe("le-package-app-1_req-1.zip");
            done.RequestId.ShouldBe("req-1");

            await repo.EnqueueAsync(Doc("doc-2", "hash-2"), "ob-2",
                "generate", Payload);
            await repo.DeadLetterAsync("ob-2", "doc-2", 5,
                "Contract: bad payload");
            var failed = await repo.GetDocAsync("doc-2");
            failed!.Status.ShouldBe("failed");
            failed.Error.ShouldContain("Contract");

            var list = await repo.ListDocsAsync("app-1");
            list.Count.ShouldBe(2);
        }
    }

    [Fact]
    public async Task List_docs_is_newest_first()
    {
        var (repo, rec, _) = await ProvisionAsync();
        using (TenantContext.Use(TenantScope.From(rec)))
        using (Lola.Application.Security.CipherContext.Use(rec.Id))
        {
            await repo.EnqueueAsync(Doc(), "ob-1", "generate", Payload);
            await repo.EnqueueAsync(Doc("doc-2", "hash-2"), "ob-2",
                "generate", Payload);
            var list = await repo.ListDocsAsync("app-1");
            // created_utc can tie at ms precision — rowid breaks it.
            list.Select(d => d.DocId).ShouldBe(["doc-2", "doc-1"]);
        }
    }
}
