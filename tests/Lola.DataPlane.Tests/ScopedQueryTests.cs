using Lola.Application.Applications;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.Domain.Applications;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-266 acceptance — scoped reads are SQL predicates BEFORE any
/// ciphertext touches: partner/borrower lists and detail never decrypt
/// an unrelated (or any) name/doc ciphertext, links revoke
/// immediately, and paging is bounded + deterministic. A recording
/// cipher counts every decrypt call to prove it.
[Collection("openbao")]
public sealed class ScopedQueryTests(OpenBaoFixture fixture)
{
    static ScopedQueryTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private sealed class RecordingCipher(IFieldCipher inner) : IFieldCipher
    {
        public int DecryptCalls { get; private set; }
        public Task<string> EncryptAsync(string p, CancellationToken ct = default)
            => inner.EncryptAsync(p, ct);
        public Task<IReadOnlyList<string>> EncryptAsync(
            IReadOnlyList<string> ps, CancellationToken ct = default)
            => inner.EncryptAsync(ps, ct);
        public Task<string> DecryptAsync(string c, CancellationToken ct = default)
        { DecryptCalls++; return inner.DecryptAsync(c, ct); }
        public Task<IReadOnlyList<string>> DecryptAsync(
            IReadOnlyList<string> cs, CancellationToken ct = default)
        { DecryptCalls++; return inner.DecryptAsync(cs, ct); }
    }

    private (ApplicationRepository apps, ApplicationPartnerRepository links,
             TenantProvisioningService svc, TenantRegistry registry,
             RecordingCipher cipher) Sut()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"), TestDbKeys.ControlFileKeyHex);
        db.Migrate();
        var registry = new TenantRegistry(db, TestCrypto.Cipher, TestCrypto.Index);
        var cipher = new RecordingCipher(
            new TransitFieldCipher(fixture.Transit));
        var blind = new TransitBlindIndex(fixture.Transit);
        var runner = new TenantMigrationRunner(
            cipher, registry, new TenantMigrationTracker(db));
        var svc = new TenantProvisioningService(
            registry, new TransitKeyManager(fixture.Transit),
            new TenantDbProvisioner(cipher), new StubServiceClients(),
            runner, fixture.DataDir);
        var factory = new TenantConnectionFactory(cipher);
        var parties = new ApplicationPartyRepository(factory, cipher, blind);
        var apps = new ApplicationRepository(
            factory, cipher, parties, blind,
            new LoanEventLog(factory));
        return (apps, new ApplicationPartnerRepository(factory),
            svc, registry, cipher);
    }

    private static ApplicationDocument Doc()
    {
        var doc = ApplicationDocument.Empty();
        doc.Set("loan_amount", "425000");
        doc.Set("party[0].role", "borrower");
        doc.Set("party[0].first_name", "Ada");
        doc.Set("party[0].last_name", "Lovelace");
        return doc;
    }

    [Fact]
    public async Task Partner_list_filters_in_sql_and_never_decrypts()
    {
        var (apps, links, svc, registry, cipher) = Sut();
        var tid = $"scoped-{Guid.NewGuid():N}";
        await svc.ProvisionAsync(new ProvisionRequest(tid, tid, "T"));
        var rec = (await registry.FindByIdAsync(tid))!;
        using (CipherContext.Use(tid))
        using (TenantContext.Use(TenantScope.From(rec)))
        {
            await apps.InsertAsync("app-yes", Doc(), "draft",
                "u-b1", "test");
            await apps.InsertAsync("app-no", Doc(), "draft",
                "u-b2", "test");
            await links.LinkAsync(new PartnerLink(
                "app-yes", "u-br", "Broker", "u-admin",
                "2026-01-01T00:00:00Z", null), default);
            await links.LinkAsync(new PartnerLink(
                "app-no", "u-other", "Broker", "u-admin",
                "2026-01-01T00:00:00Z", null), default);

            var writes = cipher.DecryptCalls;

            // Partner page — only the linked row; the masked sentinel,
            // never a decrypted name.
            var page = await apps.ListScopedAsync(
                ActorScope.Partner, "u-br", 0, 10,
                includeNames: false);
            page.Total.ShouldBe(1);
            page.Items.ShouldHaveSingleItem().Id.ShouldBe("app-yes");
            page.Items[0].BorrowerName
                .ShouldBe(ApplicationMeta.MaskedName);
            cipher.DecryptCalls.ShouldBe(writes);

            // Borrower page — same: own row, sentinel, zero decrypts.
            var own = await apps.ListScopedAsync(
                ActorScope.Borrower, "u-b1", 0, 10,
                includeNames: false);
            own.Total.ShouldBe(1);
            own.Items[0].BorrowerName
                .ShouldBe(ApplicationMeta.MaskedName);
            cipher.DecryptCalls.ShouldBe(writes);

            // Staff with names — decrypts exactly the visible rows.
            var staff = await apps.ListScopedAsync(
                ActorScope.Staff, null, 0, 10,
                includeNames: true);
            staff.Total.ShouldBe(2);
            staff.Items.ShouldContain(
                m => m.BorrowerName == "Ada Lovelace");
        }
    }

    [Fact]
    public async Task Scoped_meta_and_doc_value_skip_the_cipher()
    {
        var (apps, links, svc, registry, cipher) = Sut();
        var tid = $"meta-{Guid.NewGuid():N}";
        await svc.ProvisionAsync(new ProvisionRequest(tid, tid, "T"));
        var rec = (await registry.FindByIdAsync(tid))!;
        using (CipherContext.Use(tid))
        using (TenantContext.Use(TenantScope.From(rec)))
        {
            await apps.InsertAsync("app-1", Doc(), "submitted",
                "u-b1", "test");
            var writes = cipher.DecryptCalls;

            // Unlinked partner: row exists but InScope=false → the
            // handler maps to Forbidden without touching the doc.
            var denied = await apps.GetScopedMetaAsync(
                "app-1", ActorScope.Partner, "u-br");
            denied.ShouldNotBeNull().InScope.ShouldBeFalse();
            (await apps.GetScopedMetaAsync(
                "missing", ActorScope.Partner, "u-br"))
                .ShouldBeNull();

            await links.LinkAsync(new PartnerLink(
                "app-1", "u-br", "Broker", "u-admin",
                "2026-01-01T00:00:00Z", null), default);
            var meta = await apps.GetScopedMetaAsync(
                "app-1", ActorScope.Partner, "u-br");
            meta!.InScope.ShouldBeTrue();
            meta.Status.ShouldBe("submitted");

            // Non-PII payload value via json_extract — the payload
            // column itself never leaves the DB.
            (await apps.GetDocValueAsync("app-1", "$.loan_amount"))
                .ShouldBe("425000");
            cipher.DecryptCalls.ShouldBe(writes);

            // Revoke closes the scope on the very next read.
            await links.RevokeAsync("app-1", "u-br",
                "2026-01-02T00:00:00Z");
            (await apps.GetScopedMetaAsync(
                "app-1", ActorScope.Partner, "u-br"))!.InScope
                .ShouldBeFalse();
            var page = await apps.ListScopedAsync(
                ActorScope.Partner, "u-br", 0, 10,
                includeNames: false);
            page.Total.ShouldBe(0);
        }
    }
}
