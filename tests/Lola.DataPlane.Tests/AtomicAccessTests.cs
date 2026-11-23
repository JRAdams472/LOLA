using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Events;
using Lola.Application.Identity;
using Lola.Domain.Applications;
using Lola.Domain.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.ControlPlane;
using Lola.Infrastructure.Crypto;
using Shouldly;
using SQLitePCL;

namespace Lola.DataPlane.Tests;

/// MPE-274 acceptance — link/revoke state, tenant audit, and loan
/// event commit all-or-nothing inside one BEGIN IMMEDIATE. A probe
/// fires before each append; throwing there must leave NONE of the
/// three behind, with both hash chains still verifying on reopen.
/// No-op/retry semantics write no duplicate evidence.
[Collection("openbao")]
public sealed class AtomicAccessTests(OpenBaoFixture fixture)
{
    static AtomicAccessTests() =>
        SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlcipher());

    private string _tid = "";
    private TenantRecord _rec = null!;
    private (ApplicationPartnerRepository links, TenantAuditLog audit,
             LoanEventLog events, ApplicationRepository apps) Sut()
    {
        var db = new ControlPlaneDb(
            Path.Combine(fixture.DataDir, $"control-{Guid.NewGuid():N}.db"), TestDbKeys.ControlFileKeyHex);
        db.Migrate();
        var registry = new TenantRegistry(db);
        var cipher = new TransitFieldCipher(fixture.Transit);
        var runner = new TenantMigrationRunner(
            cipher, registry, new TenantMigrationTracker(db));
        var svc = new TenantProvisioningService(
            registry, new TransitKeyManager(fixture.Transit),
            new TenantDbProvisioner(cipher), new StubServiceClients(),
            runner, fixture.DataDir);
        var tid = $"atomic-{Guid.NewGuid():N}";
        svc.ProvisionAsync(new ProvisionRequest(tid, tid, "T"))
            .GetAwaiter().GetResult();
        _tid = tid;
        _rec = registry.FindByIdAsync(tid).GetAwaiter().GetResult()!;
        var factory = new TenantConnectionFactory(cipher);
        var blind = new TransitBlindIndex(fixture.Transit);
        var parties = new ApplicationPartyRepository(
            factory, cipher, blind);
        var events = new LoanEventLog(factory);
        return (new ApplicationPartnerRepository(factory),
            new TenantAuditLog(factory), events,
            new ApplicationRepository(
                factory, cipher, parties, blind, events));
    }

    private static PartnerLink Link(string role = "Broker") => new(
        "app-1", "u-br", role, "u-admin",
        "2026-01-01T00:00:00Z", null);

    private static AuditDraft Audit(string action) => new(
        "u-admin", action, "application", "app-1",
        ["application_partners"], null);

    private static LoanEventDraft Ev(string kind) => new(
        "app-1", kind, "u-admin", "application_partner", "u-br");

    private IDisposable Scope()
        => new Both(
            Lola.Application.Security.CipherContext.Use(_tid),
            TenantContext.Use(TenantScope.From(_rec)));

    private sealed class Both(IDisposable a, IDisposable b)
        : IDisposable
    { public void Dispose() { b.Dispose(); a.Dispose(); } }

    [Theory]
    [InlineData("state")]
    [InlineData("audit")]
    [InlineData("event")]
    public async Task Injected_failure_leaves_nothing_committed(
        string failAt)
    {
        var (links, audit, events, apps) = Sut();
        using var _ = Scope();
        await apps.InsertAsync("app-1",
            ApplicationDocument.Empty(), "draft", "u-b1", "test");

        links.WriteProbe = at =>
        { if (at == failAt) throw new InvalidOperationException("boom"); };

        await Should.ThrowAsync<InvalidOperationException>(
            () => links.CommitLinkAsync(Link(), Audit("partner_linked"),
                Ev(LoanEventKind.PartnerLinked)));

        (await links.ListForAppAsync("app-1")).ShouldBeEmpty();
        (await audit.VerifyChainAsync()).EventCount.ShouldBe(0);
        (await events.VerifyChainAsync()).EventCount.ShouldBe(0);
        (await audit.VerifyChainAsync()).Ok.ShouldBeTrue();
        (await events.VerifyChainAsync()).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Retry_and_relink_write_evidence_exactly_once()
    {
        var (links, audit, events, apps) = Sut();
        using var _ = Scope();
        await apps.InsertAsync("app-1",
            ApplicationDocument.Empty(), "draft", "u-b1", "test");

        (await links.CommitLinkAsync(Link(), Audit("partner_linked"),
            Ev(LoanEventKind.PartnerLinked)))
            .ShouldBe(PartnerLinkOutcome.Linked);
        // Identical retry — NoOp, no duplicate evidence.
        (await links.CommitLinkAsync(Link(), Audit("partner_linked"),
            Ev(LoanEventKind.PartnerLinked)))
            .ShouldBe(PartnerLinkOutcome.NoOp);
        (await audit.QueryAsync(new AuditFilter())).Count(e =>
            e.Action == "partner_linked").ShouldBe(1);
        (await events.QueryAsync("app-1")).Count.ShouldBe(1);

        // Revoke then re-link — both transitions earn evidence.
        (await links.CommitRevokeAsync("app-1", "u-br",
            "2026-01-02T00:00:00Z", Audit("partner_revoked"),
            Ev(LoanEventKind.PartnerRevoked)))
            .ShouldBe(PartnerLinkOutcome.Revoked);
        (await links.CommitLinkAsync(Link(), Audit("partner_linked"),
            Ev(LoanEventKind.PartnerLinked)))
            .ShouldBe(PartnerLinkOutcome.Linked);

        // Second revoke on the now-active link is NOT a no-op.
        (await links.CommitRevokeAsync("app-1", "u-br",
            "2026-01-03T00:00:00Z", Audit("partner_revoked"),
            Ev(LoanEventKind.PartnerRevoked)))
            .ShouldBe(PartnerLinkOutcome.Revoked);
        // Third — nothing active → NotFound, nothing written.
        (await links.CommitRevokeAsync("app-1", "u-br",
            "2026-01-04T00:00:00Z", Audit("partner_revoked"),
            Ev(LoanEventKind.PartnerRevoked)))
            .ShouldBe(PartnerLinkOutcome.NotFound);

        (await audit.QueryAsync(new AuditFilter()))
            .Count(e => e.Action is "partner_linked" or "partner_revoked")
            .ShouldBe(4);
        var rows = await links.ListForAppAsync("app-1");
        rows.ShouldHaveSingleItem().RevokedUtc.ShouldBe("2026-01-03T00:00:00Z");
        (await audit.VerifyChainAsync()).Ok.ShouldBeTrue();
        (await events.VerifyChainAsync()).Ok.ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_links_do_not_fork_either_chain()
    {
        var (links, audit, events, apps) = Sut();
        using var _ = Scope();
        await apps.InsertAsync("app-1",
            ApplicationDocument.Empty(), "draft", "u-b1", "test");

        // Five writers race the same link — single-file SQLite makes
        // the losers busy-wait, so all five commits serialize; exactly
        // one earns Linked and one evidence row lands.
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(
            _ => links.CommitLinkAsync(Link(),
                Audit("partner_linked"), Ev(LoanEventKind.PartnerLinked))));
        results.Count(r => r == PartnerLinkOutcome.Linked)
            .ShouldBe(1);
        results.Count(r => r == PartnerLinkOutcome.NoOp)
            .ShouldBe(4);
        (await audit.VerifyChainAsync()).Ok.ShouldBeTrue();
        (await events.VerifyChainAsync()).Ok.ShouldBeTrue();
    }
}
