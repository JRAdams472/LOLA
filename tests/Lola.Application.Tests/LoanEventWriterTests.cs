using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Events;
using Lola.Application.Security;
using Lola.Domain.Applications;
using Lola.Domain.Classification;
using Shouldly;

namespace Lola.Application.Tests;

/// Capturing `ILoanEventLog` for handler tests — proves which event
/// kinds fire without a tenant file.
internal sealed class FakeEventLog : ILoanEventLog
{
    public List<LoanEventDraft> Drafts { get; } = [];
    public Task WriteAsync(LoanEventDraft draft, CancellationToken ct = default)
    { Drafts.Add(draft); return Task.CompletedTask; }
    public Task<EventVerifyResult> VerifyChainAsync(CancellationToken ct = default)
        => Task.FromResult(new EventVerifyResult(true, null, Drafts.Count));
    public Task<IReadOnlyList<LoanEvent>> QueryAsync(string applicationId,
        int limit = 200, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<LoanEvent>>([]);
}

/// M15 P2 — `LoanEventWriter`: per-path diffs against the pre-merge
/// doc, blind-index HMAC for PII classes, no-op patches emit nothing.
public sealed class LoanEventWriterTests
{
    private sealed class FakeIndex : IBlindIndex
    {
        public Task<string> IndexAsync(PiiClass classification,
            string value, CancellationToken ct = default)
            => Task.FromResult($"idx:v1:{classification}:{value.GetHashCode():x8}");
    }

    private static ApplicationDocument Doc()
    {
        var doc = ApplicationDocument.Empty();
        doc.Set("loan.amount", "1000");
        doc.Set("party[0].ssn", "111-22-3333");
        return doc;
    }

    [Fact]
    public async Task Pii_and_plain_paths_encode_differently()
    {
        var patch = new Dictionary<string, string>
        {
            ["party[0].ssn"] = "444-55-6666",
            ["loan.amount"] = "1200",
        };
        var drafts = await LoanEventWriter.FieldChangesAsync("app-1",
            "officer-1", Doc(), patch, new FakeIndex());
        drafts.Count.ShouldBe(2);

        var ssn = drafts.Single(d => d.FieldPath == "party[0].ssn");
        ssn.EventKind.ShouldBe(LoanEventKind.FieldChange);
        ssn.FieldClass.ShouldBe(PiiClass.Ssn);
        ssn.BeforeVal.ShouldStartWith("idx:v1:Ssn:");
        ssn.AfterVal.ShouldStartWith("idx:v1:Ssn:");
        ssn.BeforeVal.ShouldNotContain("111-22-3333");
        ssn.AfterVal.ShouldNotContain("444-55-6666");

        var amt = drafts.Single(d => d.FieldPath == "loan.amount");
        amt.FieldClass.ShouldBeNull();
        amt.BeforeVal.ShouldBe("1000");
        amt.AfterVal.ShouldBe("1200");
    }

    [Fact]
    public async Task Noop_patch_emits_nothing()
    {
        var patch = new Dictionary<string, string>
        { ["loan.amount"] = "1000" }; // same as before
        var drafts = await LoanEventWriter.FieldChangesAsync("app-1",
            "officer-1", Doc(), patch, new FakeIndex());
        drafts.ShouldBeEmpty();
    }

    [Fact]
    public async Task New_field_has_null_before()
    {
        var patch = new Dictionary<string, string>
        { ["loan.purpose"] = "refinance" };
        var drafts = await LoanEventWriter.FieldChangesAsync("app-1",
            "officer-1", Doc(), patch, new FakeIndex());
        var d = drafts.Single();
        d.BeforeVal.ShouldBeNull();
        d.AfterVal.ShouldBe("refinance");
    }
}

/// M15 P2 — handler emission: transition + reveal each write their
/// event kind through the log.
public sealed class LoanEventEmissionTests
{
    private sealed class FakeApps(ApplicationRecord record)
        : IApplicationRepository
    {
        public Task<ApplicationPage> ListScopedAsync(
            ActorScope scope, string? actorId, int skip, int take,
            bool includeNames, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<ScopedApplicationMeta?> GetScopedMetaAsync(
            string id, ActorScope scope, string? actorId,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<string?> GetDocValueAsync(
            string id, string path, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> IsPartnerLinkedAsync(string id,
            string partnerUserId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> LinkedAppIdsForPartnerAsync(
            string partnerUserId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ApplicationRecord?> GetAsync(string id,
            CancellationToken ct = default)
            => Task.FromResult<ApplicationRecord?>(
                id == record.Id ? record : null);
        public Task UpdatePipelineStepAsync(string id, string s,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordStepEventAsync(string id, string s, string e,
            string a, string? r, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task InsertAsync(string id, ApplicationDocument d,
            string s, string? b, string w, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> WriteVersionAsync(string id,
            ApplicationDocument d, string s, string w, bool sub,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<(int Version, ApplicationDocument Doc)>
            PatchDraftAsync(string id,
            IReadOnlyDictionary<string, string> patch, string writtenBy,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdateStatusAsync(string id, string s,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task EnsureTridClockStartAsync(string id,
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
    }

    private sealed class FakeAudit : ITenantAuditLog
    {
        public Task WriteAsync(string actorId, string action,
            string entity, string? entityId,
            IReadOnlyList<string> fieldClasses, string? reason,
            CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<AuditEvent>> QueryAsync(
            AuditFilter filter, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<AuditVerifyResult> VerifyChainAsync(
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task Pipeline_advance_emits_transition()
    {
        var record = new ApplicationRecord("app-1", "submitted",
            "POSIntake", null, 1, null, null,
            ApplicationDocument.Empty());
        var events = new FakeEventLog();
        var eval = new Tasks.TaskEvaluator(new FakeConditionRepository(),
            new FakeApps(record), new FakeTaskRepository(),
            TimeProvider.System);
        var pipe = new WorkflowPipeline(new FakeApps(record), events, eval);
        await pipe.AdvanceAsync(record, "Validate", "officer-1",
            "intake complete");
        var e = events.Drafts.Single();
        e.EventKind.ShouldBe(LoanEventKind.Transition);
        e.FieldPath.ShouldBe("pipeline_step");
        e.BeforeVal.ShouldBe("POSIntake");
        e.AfterVal.ShouldBe("Validate");
    }

    [Fact]
    public async Task Reveal_emits_one_event_per_pii_class()
    {
        var doc = ApplicationDocument.Empty();
        doc.Set("party[0].first_name", "Ada");
        doc.Set("party[0].ssn", "111-22-3333");
        var record = new ApplicationRecord("app-1", "submitted",
            "Doc", null, 3, null, "2026-01-01T00:00:00Z", doc);
        var events = new FakeEventLog();
        using var _ = Lola.Application.Tenancy.TenantContext.Use(
            new Lola.Application.Tenancy.TenantScope("t1", "x.db", "k"));
        var dto = await RevealApplicationHandler.Handle(
            new RevealApplicationQuery("t1", "app-1", "officer-1",
                "borrower support call", ActorScope.Staff),
            new FakeApps(record), new FakeAudit(), events, default);
        dto.ShouldNotBeNull();
        // Ssn + Name — distinct classes, never values.
        events.Drafts.Select(d => d.EventKind).Distinct()
            .ShouldBe([LoanEventKind.Reveal]);
        events.Drafts.Select(d => d.FieldClass).OrderBy(c => c)
            .ShouldBe([PiiClass.Name, PiiClass.Ssn]);
        events.Drafts.ShouldAllBe(
            d => d.BeforeVal == null && d.AfterVal == null);
    }
}
