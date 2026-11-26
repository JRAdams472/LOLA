using Lola.Application.Applications;
using Lola.Application.Compliance;
using Lola.Application.Errors;
using Lola.Application.Events;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Domain.Applications;
using Shouldly;
using Xunit;

namespace Lola.Application.Tests;

/// M23 P2 (MPE-310) — RESPA wiring: escrow analysis pipeline,
/// servicing-transfer notices, overdue-condition dedupe, AfBA flag,
/// and the negative-case matrix.
public sealed class RespaWiringTests
{
    // -- fakes ------------------------------------------------------

    private sealed class FakeRespa : IRespaRepository
    {
        public List<EscrowAnalysisRecord> Analyses { get; } = [];
        public List<ServicingNoticeRecord> Notices { get; } = [];
        public List<ServicingNoticeEventRecord> Events { get; } = [];

        private static string Stamp(int n)
            => $"2026-01-01T00:00:{n % 60:00}.000Z";

        public Task<EscrowAnalysisRecord> AddAnalysisAsync(string appId,
            string verdict, decimal? annualDisbursements,
            decimal? cushionTarget, decimal? lowPoint,
            decimal? determinationAmount, bool refundRequired,
            string inputsJson, string detailJson, string actor,
            CancellationToken ct)
        {
            var r = new EscrowAnalysisRecord($"esc-{Analyses.Count + 1}",
                appId, verdict, (double?)annualDisbursements,
                (double?)cushionTarget, (double?)lowPoint,
                (double?)determinationAmount, refundRequired ? 1 : 0,
                inputsJson, detailJson, actor, Stamp(Analyses.Count));
            Analyses.Add(r);
            return Task.FromResult(r);
        }

        public Task<IReadOnlyList<EscrowAnalysisRecord>>
            ListAnalysesAsync(string appId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EscrowAnalysisRecord>>(
                Analyses.Where(r => r.ApplicationId == appId)
                    .OrderByDescending(r => r.CreatedUtc).ToList());

        public Task<ServicingNoticeRecord> AddNoticeAsync(string appId,
            string noticeKind, string transferEffective,
            string transferorLabel, string transfereeLabel,
            string sendBy, string actor, CancellationToken ct)
        {
            var id = $"ntc-{Notices.Count + 1}";
            var r = new ServicingNoticeRecord(id, appId, noticeKind,
                transferEffective, transferorLabel, transfereeLabel,
                sendBy, "pending", actor, Stamp(Notices.Count));
            Notices.Add(r);
            Events.Add(new ServicingNoticeEventRecord(
                $"nev-{Events.Count + 1}", id, "pending", actor,
                Stamp(Events.Count)));
            return Task.FromResult(r);
        }

        public Task<ServicingNoticeRecord?> GetNoticeAsync(
            string noticeId, CancellationToken ct)
            => Task.FromResult(Notices
                .FirstOrDefault(n => n.NoticeId == noticeId));

        public Task<IReadOnlyList<ServicingNoticeRecord>>
            ListNoticesAsync(string appId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ServicingNoticeRecord>>(
                Notices.Where(n => n.ApplicationId == appId).ToList());

        public Task<string> TransitionNoticeAsync(string noticeId,
            string status, string actor, CancellationToken ct)
        {
            var idx = Notices.FindIndex(n => n.NoticeId == noticeId);
            var stamp = Stamp(Events.Count + 30);
            if (idx >= 0)
                Notices[idx] = Notices[idx] with { Status = status };
            Events.Add(new ServicingNoticeEventRecord(
                $"nev-{Events.Count + 1}", noticeId, status, actor,
                stamp));
            return Task.FromResult(stamp);
        }

        public Task<IReadOnlyList<ServicingNoticeEventRecord>>
            ListNoticeEventsAsync(string noticeId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<
                ServicingNoticeEventRecord>>(
                Events.Where(e => e.NoticeId == noticeId).ToList());
    }

    private sealed class FakeApps : IApplicationRepository
    {
        public ApplicationDocument Doc = ApplicationDocument.Empty();
        public Task<ApplicationRecord?> GetAsync(string id,
            CancellationToken ct = default)
            => Task.FromResult<ApplicationRecord?>(new(id, "draft",
                "intake", null, 1, null, null, Doc));
        public Task InsertAsync(string id, ApplicationDocument doc,
            string status, string? borrowerUserId, string writtenBy,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> WriteVersionAsync(string id,
            ApplicationDocument doc, string status, string writtenBy,
            bool submitted, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<(int Version, ApplicationDocument Doc)>
            PatchDraftAsync(string id,
                IReadOnlyDictionary<string, string> patch,
                string writtenBy, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdateStatusAsync(string id, string status,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task EnsureTridClockStartAsync(string id,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdatePipelineStepAsync(string id, string step,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RecordStepEventAsync(string id, string step,
            string event_, string actor, string? reason,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
            string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationRetentionRow>>
            RetentionWindowsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationMeta>> ListAsync(
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> IsPartnerLinkedAsync(string appId,
            string partnerUserId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<string>>
            LinkedAppIdsForPartnerAsync(string partnerUserId,
                CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ApplicationPage> ListScopedAsync(
            ActorScope scope, string? actorId, int skip, int take,
            bool includeNames, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ScopedApplicationMeta?> GetScopedMetaAsync(
            string id, ActorScope scope, string? actorId,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<string?> GetDocValueAsync(string id, string path,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task LinkAsync(PartnerLink link,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<bool> RevokeAsync(string appId,
            string partnerUserId, string revokedUtc,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<PartnerLink>> ListForAppAsync(
            string appId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<PartnerLinkOutcome> CommitLinkAsync(
            PartnerLink link, Audit.AuditDraft audit,
            Events.LoanEventDraft loanEvent,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<PartnerLinkOutcome> CommitRevokeAsync(string appId,
            string partnerUserId, string revokedUtc,
            Audit.AuditDraft audit, Events.LoanEventDraft loanEvent,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private static IDisposable Scope()
        => TenantContext.Use(new TenantScope("t1", "x.db", "k"));

    /// Escrow fixture matching the domain suite: $3,600 tax + $1,200
    /// insurance, $400/mo payment, $800 balance — exactly within.
    private static ApplicationDocument EscrowDoc()
    {
        var d = ApplicationDocument.Empty();
        d.Set("escrow.current_balance", "800");
        d.Set("escrow.monthly_payment", "400");
        d.Set("escrow.annual_property_tax", "3600");
        d.Set("escrow.annual_hazard_insurance", "1200");
        return d;
    }

    // -- RunEscrowAnalysis ------------------------------------------

    [Fact]
    public async Task Analyze_writes_row_and_event_with_verdict()
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = EscrowDoc() };

        var dto = await RunEscrowAnalysisHandler.Handle(
            new RunEscrowAnalysisCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, respa, events, default);

        dto.Verdict.ShouldBe("within");
        dto.AnnualDisbursements.ShouldBe(4800m);
        respa.Analyses.ShouldHaveSingleItem();
        var draft = events.Drafts.ShouldHaveSingleItem();
        draft.EventKind.ShouldBe(LoanEventKind.EscrowAnalysis);
        draft.AfterVal.ShouldBe("verdict:within");
        // invariant 3 — the event carries no figures.
        draft.AfterVal!.ShouldNotContain("4800");
    }

    [Fact]
    public async Task Missing_inputs_still_land_as_incomplete()
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = ApplicationDocument.Empty() };

        var dto = await RunEscrowAnalysisHandler.Handle(
            new RunEscrowAnalysisCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, respa, events, default);

        dto.Verdict.ShouldBe("incomplete");
        dto.MissingInputs.ShouldNotBeEmpty();
        respa.Analyses.ShouldHaveSingleItem();
        events.Drafts.ShouldHaveSingleItem()
            .AfterVal!.ShouldContain("incomplete");
    }

    [Fact]
    public async Task Two_analyses_append_two_rows_and_two_events()
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = EscrowDoc() };

        await RunEscrowAnalysisHandler.Handle(
            new RunEscrowAnalysisCommand("t1", "app-1", "officer-1",
                ActorScope.Staff), apps, respa, events, default);
        await RunEscrowAnalysisHandler.Handle(
            new RunEscrowAnalysisCommand("t1", "app-1", "officer-1",
                ActorScope.Staff), apps, respa, events, default);

        respa.Analyses.Count.ShouldBe(2);
        events.Drafts.Count.ShouldBe(2);
    }

    // -- GetApplicationRespa -----------------------------------------

    [Fact]
    public async Task Report_returns_latest_plus_lineage()
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var conditions = new FakeConditionRepository();
        var apps = new FakeApps { Doc = EscrowDoc() };

        await RunEscrowAnalysisHandler.Handle(
            new RunEscrowAnalysisCommand("t1", "app-1", "officer-1",
                ActorScope.Staff), apps, respa, events, default);
        await RunEscrowAnalysisHandler.Handle(
            new RunEscrowAnalysisCommand("t1", "app-1", "officer-1",
                ActorScope.Staff), apps, respa, events, default);

        var report = await GetApplicationRespaHandler.Handle(
            new GetApplicationRespaQuery("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, respa, conditions, events, default);

        report.LatestAnalysis.ShouldNotBeNull();
        report.AnalysisHistory.Count.ShouldBe(2);
        report.AfbaDisclosureRequired.ShouldBeFalse();
        report.Notices.ShouldBeEmpty();
    }

    [Fact]
    public async Task Afba_flag_surfaces_when_provider_affiliated()
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var conditions = new FakeConditionRepository();
        var doc = EscrowDoc();
        doc.Set("settlement_provider[0].service_type", "title");
        doc.Set("settlement_provider[0].provider_name", "Affiliated Title");
        doc.Set("settlement_provider[0].affiliated_with_lender", "true");
        var apps = new FakeApps { Doc = doc };

        var report = await GetApplicationRespaHandler.Handle(
            new GetApplicationRespaQuery("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, respa, conditions, events, default);

        report.AfbaDisclosureRequired.ShouldBeTrue();
    }

    // -- Servicing transfer ------------------------------------------

    [Fact]
    public async Task Transfer_creates_both_notices_with_deadlines()
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var apps = new FakeApps();

        var notices = await RecordServicingTransferHandler.Handle(
            new RecordServicingTransferCommand("t1", "app-1",
                "officer-1", ActorScope.Staff, "2025-03-20",
                "Old Servicer", "New Servicer"),
            apps, respa, events, default);

        notices.Count.ShouldBe(2);
        var transferor = notices.Single(
            n => n.NoticeKind == "transferor");
        var transferee = notices.Single(
            n => n.NoticeKind == "transferee");
        transferor.SendBy.ShouldBe("2025-03-05");  // effective - 15d
        transferee.SendBy.ShouldBe("2025-04-04");  // effective + 15d
        events.Drafts.Count.ShouldBe(2);
        respa.Events.Count(e => e.Status == "pending").ShouldBe(2);
    }

    [Fact]
    public async Task Sent_then_received_transitions_with_events()
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var apps = new FakeApps();

        var notices = await RecordServicingTransferHandler.Handle(
            new RecordServicingTransferCommand("t1", "app-1",
                "officer-1", ActorScope.Staff, "2025-03-20",
                "Old", "New"),
            apps, respa, events, default);
        var noticeId = notices[0].NoticeId;

        var sent = await MarkNoticeSentHandler.Handle(
            new MarkNoticeSentCommand("t1", "app-1", noticeId,
                "officer-1", ActorScope.Staff),
            apps, respa, events, default);
        sent.Status.ShouldBe("sent");
        sent.SentUtc.ShouldNotBeNull();

        var received = await MarkNoticeReceivedHandler.Handle(
            new MarkNoticeReceivedCommand("t1", "app-1", noticeId,
                "officer-1", ActorScope.Staff),
            apps, respa, events, default);
        received.Status.ShouldBe("received");
        received.ReceivedUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task Received_before_sent_is_a_conflict()
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var apps = new FakeApps();

        var notices = await RecordServicingTransferHandler.Handle(
            new RecordServicingTransferCommand("t1", "app-1",
                "officer-1", ActorScope.Staff, "2025-03-20",
                "Old", "New"),
            apps, respa, events, default);

        var ex = await Should.ThrowAsync<DomainException>(
            MarkNoticeReceivedHandler.Handle(
                new MarkNoticeReceivedCommand("t1", "app-1",
                    notices[0].NoticeId, "officer-1", ActorScope.Staff),
                apps, respa, events, default));
        ex.Message.ShouldContain("pending");
    }

    [Fact]
    public async Task Sent_on_nonexistent_notice_is_404()
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var apps = new FakeApps();

        var ex = await Should.ThrowAsync<DomainException>(
            MarkNoticeSentHandler.Handle(
                new MarkNoticeSentCommand("t1", "app-1", "ntc-nope",
                    "officer-1", ActorScope.Staff),
                apps, respa, events, default));
        ex.Message.ShouldContain("not found");
    }

    // -- Overdue sweep + dedupe ---------------------------------------

    [Fact]
    public async Task Overdue_notice_opens_condition_exactly_once()
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var conditions = new FakeConditionRepository();
        var apps = new FakeApps();

        // Transfer effective long past — both notices' deadlines gone.
        await RecordServicingTransferHandler.Handle(
            new RecordServicingTransferCommand("t1", "app-1",
                "officer-1", ActorScope.Staff, "2020-01-15",
                "Old", "New"),
            apps, respa, events, default);

        var first = await GetApplicationRespaHandler.Handle(
            new GetApplicationRespaQuery("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, respa, conditions, events, default);
        first.Notices.ShouldAllBe(n => n.Overdue);
        var openCount = conditions.Store.Count(c =>
            c.Kind == ConditionKind.ServicingNotice
            && c.State == ConditionState.Open);
        openCount.ShouldBe(2); // one per notice, keyed by linked entity

        // Second read — the sweep must not double-open.
        await GetApplicationRespaHandler.Handle(
            new GetApplicationRespaQuery("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, respa, conditions, events, default);
        conditions.Store.Count(c =>
            c.Kind == ConditionKind.ServicingNotice
            && c.State == ConditionState.Open).ShouldBe(2);
    }

    [Fact]
    public async Task Sent_notice_is_not_flagged_overdue()
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var conditions = new FakeConditionRepository();
        var apps = new FakeApps();

        var notices = await RecordServicingTransferHandler.Handle(
            new RecordServicingTransferCommand("t1", "app-1",
                "officer-1", ActorScope.Staff, "2020-01-15",
                "Old", "New"),
            apps, respa, events, default);
        await MarkNoticeSentHandler.Handle(
            new MarkNoticeSentCommand("t1", "app-1",
                notices[0].NoticeId, "officer-1", ActorScope.Staff),
            apps, respa, events, default);

        var report = await GetApplicationRespaHandler.Handle(
            new GetApplicationRespaQuery("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, respa, conditions, events, default);
        report.Notices.Single(n => n.NoticeId == notices[0].NoticeId)
            .Overdue.ShouldBeFalse();
        // The still-pending transferee notice remains overdue.
        report.Notices.Single(n => n.NoticeId == notices[1].NoticeId)
            .Overdue.ShouldBeTrue();
    }

    // -- scope guards --------------------------------------------------

    [Theory]
    [InlineData(ActorScope.Borrower)]
    [InlineData(ActorScope.Partner)]
    public async Task Non_staff_are_forbidden_everywhere(ActorScope scope)
    {
        using var _ = Scope();
        var respa = new FakeRespa();
        var events = new FakeEventLog();
        var conditions = new FakeConditionRepository();
        var apps = new FakeApps();

        await Should.ThrowAsync<DomainException>(
            RunEscrowAnalysisHandler.Handle(
                new RunEscrowAnalysisCommand("t1", "app-1", "u", scope),
                apps, respa, events, default));
        await Should.ThrowAsync<DomainException>(
            GetApplicationRespaHandler.Handle(
                new GetApplicationRespaQuery("t1", "app-1", "u", scope),
                apps, respa, conditions, events, default));
        await Should.ThrowAsync<DomainException>(
            RecordServicingTransferHandler.Handle(
                new RecordServicingTransferCommand("t1", "app-1", "u",
                    scope, "2025-03-20", "a", "b"),
                apps, respa, events, default));
    }
}
