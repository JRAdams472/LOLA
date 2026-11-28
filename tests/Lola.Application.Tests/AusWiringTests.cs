using System.Net;
using System.Text.Json.Nodes;
using Lola.Application.Applications;
using Lola.Application.Aus;
using Lola.Application.Compliance;
using Lola.Application.Errors;
using Lola.Application.Events;
using Lola.Application.Tenancy;
using Lola.Application.Vendors;
using Lola.Domain.Applications;
using Shouldly;
using Xunit;

namespace Lola.Application.Tests;

/// M25 P2 (MPE-316) — AUS wiring: completion processing (results,
/// findings, condition auto-population + dedupe), posture read,
/// submit validation, adapter contract against fixtures, HMDA linkage.
public sealed class AusWiringTests
{
    // -- fakes ------------------------------------------------------

    private sealed class FakeAusStore : IAusResultsStore
    {
        public List<AusResultRow> Results { get; } = [];
        public List<AusFindingRow> Findings { get; } = [];

        public Task<AusResultRow> AddResultAsync(string appId,
            string vendor, string? orderId, string? caseId,
            string recommendation, string? riskClass,
            string? responseXml, string responseHash, int findingsCount,
            string actor, CancellationToken ct)
        {
            var row = new AusResultRow($"aus-{Results.Count + 1}", appId,
                vendor, orderId, caseId, recommendation, riskClass,
                findingsCount, $"2026-01-01T00:00:{Results.Count:00}.000Z",
                actor);
            Results.Add(row);
            return Task.FromResult(row);
        }

        public Task AddFindingAsync(string resultId, string vendorCode,
            string category, string severity, string message,
            string? conditionId, CancellationToken ct)
        {
            Findings.Add(new AusFindingRow($"auf-{Findings.Count + 1}",
                resultId, vendorCode, category, severity, message,
                conditionId, "2026-01-01T00:00:00.000Z"));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AusResultRow>> ListResultsAsync(
            string appId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AusResultRow>>(
                Results.Where(r => r.ApplicationId == appId).ToList());

        public Task<IReadOnlyList<AusFindingRow>> ListFindingsAsync(
            string resultId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AusFindingRow>>(
                Findings.Where(f => f.ResultId == resultId).ToList());
    }

    private sealed class FakeConditions : IConditionRepository
    {
        public List<Condition> Opened { get; } = [];

        public Task<Condition> OpenAsync(ConditionDraft draft,
            CancellationToken ct = default)
        {
            var c = new Condition($"cond-{Opened.Count + 1}",
                draft.ApplicationId, draft.Kind, "open", draft.Blocking,
                draft.LinkedEntity, draft.LinkedEntityId, draft.Title,
                draft.Detail, draft.OpenedBy, "2026-01-01T00:00:00.000Z",
                null, null, null);
            Opened.Add(c);
            return Task.FromResult(c);
        }

        public Task<Condition?> GetAsync(string conditionId,
            CancellationToken ct = default)
            => Task.FromResult(Opened.FirstOrDefault(
                c => c.ConditionId == conditionId));

        public Task<IReadOnlyList<Condition>> ListByApplicationAsync(
            string applicationId, string? state = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Condition>>(
                Opened.Where(c => c.ApplicationId == applicationId
                    && (state is null || c.State == state)).ToList());

        public Task<Condition> TransitionAsync(string conditionId,
            string toState, string? reason, CancellationToken ct = default)
            => throw new NotImplementedException();
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

    private static ApplicationDocument HmdaDoc()
    {
        var d = ApplicationDocument.Empty();
        d.Set("loan_amount", "300000");
        d.Set("loan_purpose", "purchase");
        d.Set("loan.product_type", "conventional");
        d.Set("interest_rate", "6.5");
        d.Set("loan_term_months", "360");
        d.Set("loan.cltv", "80");
        d.Set("subject_property.usage", "principal_residence");
        d.Set("subject_property.construction_method", "site_built");
        d.Set("subject_property.est_value", "375000");
        d.Set("subject_property.state", "IL");
        d.Set("subject_property.census_tract", "1234.56");
        d.Set("subject_property.units", "1");
        d.Set("demographics.ethnicity_1", "not_hispanic_or_latino");
        d.Set("demographics.race_1", "white");
        d.Set("demographics.sex", "female");
        d.Set("party[0].dob", "1980-06-15");
        d.Set("hmda.action_taken", "originated");
        d.Set("hmda.action_taken_date", "2026-04-15");
        d.Set("hmda.income", "96000");
        return d;
    }

    private static readonly HmdaOptions Opts = new();

    private static JsonObject Report(string vendor, string recommendation,
        params (string code, string category, string severity, string message)[] findings)
    {
        var arr = new JsonArray();
        foreach (var f in findings)
            arr.Add(new JsonObject
            {
                ["code"] = f.code,
                ["category"] = f.category,
                ["severity"] = f.severity,
                ["message"] = f.message
            });
        return new JsonObject
        {
            ["ausVendor"] = vendor,
            ["recommendation"] = recommendation,
            ["riskClassification"] = "accept",
            ["caseId"] = "case-1",
            ["responseXml"] = "<synthetic/>",
            ["findings"] = arr
        };
    }

    // -- completion processing ---------------------------------------

    [Fact]
    public async Task Completion_writes_result_and_findings_rows()
    {
        var store = new FakeAusStore();
        var conditions = new FakeConditions();
        var id = await ProcessAusCompletion.Handle("app-1", "vo-1", "du",
            Report("du", "approve-eligible",
                ("3893", "credit", "info", "credit score note")),
            "officer-1", store, conditions, CancellationToken.None);
        id.ShouldNotBeNull();
        var row = store.Results.ShouldHaveSingleItem();
        row.Vendor.ShouldBe("du");
        row.Recommendation.ShouldBe("approve-eligible");
        row.FindingsCount.ShouldBe(1);
        store.Findings.ShouldHaveSingleItem().VendorCode.ShouldBe("3893");
    }

    [Fact]
    public async Task Required_and_fatal_findings_open_conditions_info_does_not()
    {
        var store = new FakeAusStore();
        var conditions = new FakeConditions();
        await ProcessAusCompletion.Handle("app-1", "vo-1", "lpa",
            Report("lpa", "caution",
                ("A1", "income", "required", "verify income"),
                ("A2", "asset", "fatal", "funds missing"),
                ("A3", "credit", "info", "fyi only")),
            "officer-1", store, conditions, CancellationToken.None);
        conditions.Opened.Count.ShouldBe(2);
        conditions.Opened.ShouldAllBe(c => c.Kind == ConditionKind.AusFinding);
        var fatal = conditions.Opened.Single(c => c.Detail!.Contains("A2"));
        fatal.Blocking.ShouldBeTrue();
        // findings rows link back to their conditions
        store.Findings.Count(f => f.ConditionId is not null).ShouldBe(2);
    }

    [Fact]
    public async Task Repeat_completion_dedupes_conditions_by_vendor_code()
    {
        var store = new FakeAusStore();
        var conditions = new FakeConditions();
        var report = Report("du", "refer",
            ("X1", "eligibility", "required", "verify occupancy"));
        await ProcessAusCompletion.Handle("app-1", "vo-1", "du", report,
            "officer-1", store, conditions, CancellationToken.None);
        await ProcessAusCompletion.Handle("app-1", "vo-2", "du", report,
            "officer-1", store, conditions, CancellationToken.None);
        // append-only results land twice; the condition opens ONCE
        store.Results.Count.ShouldBe(2);
        conditions.Opened.Count.ShouldBe(1);
        // second finding row links the SAME condition
        store.Findings.Count.ShouldBe(2);
        store.Findings.Select(f => f.ConditionId).Distinct().Count()
            .ShouldBe(1);
    }

    // -- posture read -------------------------------------------------

    [Fact]
    public async Task Posture_groups_latest_per_vendor_with_lineage()
    {
        using var scope = Scope();
        var store = new FakeAusStore();
        var conditions = new FakeConditions();
        await ProcessAusCompletion.Handle("app-1", "vo-1", "du",
            Report("du", "approve-eligible"), "o", store, conditions,
            CancellationToken.None);
        await ProcessAusCompletion.Handle("app-1", "vo-2", "lpa",
            Report("lpa", "caution",
                ("L1", "income", "required", "verify income")),
            "o", store, conditions, CancellationToken.None);

        var dto = await GetApplicationAusHandler.Handle(
            new GetApplicationAusQuery("t1", "app-1", "officer-1",
                ActorScope.Staff),
            new FakeApps(), store, CancellationToken.None);
        dto.Vendors.Count.ShouldBe(2);
        dto.Vendors.Single(v => v.Vendor == "lpa").Findings
            .ShouldHaveSingleItem().VendorCode.ShouldBe("L1");
        dto.Lineage.Count.ShouldBe(2);
        // raw response XML never leaves the posture surface
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        json.ShouldNotContain("responseXml");
        json.ShouldNotContain("<synthetic");
    }

    [Fact]
    public async Task Posture_non_staff_forbidden()
    {
        using var scope = Scope();
        await Should.ThrowAsync<DomainException>(() =>
            GetApplicationAusHandler.Handle(
                new GetApplicationAusQuery("t1", "app-1", "user-1",
                    ActorScope.Borrower),
                new FakeApps(), new FakeAusStore(),
                CancellationToken.None));
    }

    // -- submit validation --------------------------------------------

    [Fact]
    public async Task Submit_rejects_unknown_vendor_before_touching_deps()
    {
        using var scope = Scope();
        var ex = await Should.ThrowAsync<DomainException>(() =>
            SubmitAusHandler.Handle(
                new SubmitAusCommand("t1", "app-1", "o", ActorScope.Staff,
                    "not-a-vendor"),
                null!, null!, null!, null!, null!, CancellationToken.None));
        ex.Message.ShouldContain("unknown aus vendor");
    }

    [Fact]
    public async Task Submit_non_staff_forbidden()
    {
        using var scope = Scope();
        await Should.ThrowAsync<DomainException>(() =>
            SubmitAusHandler.Handle(
                new SubmitAusCommand("t1", "app-1", "o",
                    ActorScope.Borrower, "du"),
                null!, null!, null!, null!, null!, CancellationToken.None));
    }

    // -- mapper: MISMO wire payload -----------------------------------

    [Fact]
    public void Mapper_aus_request_carries_mismo_xml_not_raw_doc()
    {
        var doc = ApplicationDocument.Empty();
        doc.Set("party[0].first_name", "Ava");
        doc.Set("party[0].last_name", "Tester");
        doc.Set("party[0].ssn", "000-00-0000");
        doc.Set("party[0].dob", "1985-04-12");
        doc.Set("loan_amount", "300000");
        doc.Set("loan_purpose", "Purchase");
        doc.Set("subject_property.street", "1 Main");
        doc.Set("subject_property.city", "Springfield");
        doc.Set("subject_property.state", "IL");
        doc.Set("subject_property.zip", "62701");
        var app = new ApplicationRecord("app-1", "draft", "intake", null,
            1, null, null, doc);
        var request = (AusOrderRequest)VendorRequestMapper.Map(
            VendorKind.Aus, app);
        request.MismoJson.ShouldContain("MESSAGE");
        request.MismoJson.ShouldContain("BaseLoanAmount");
        request.MismoJson.ShouldNotContain("ssn");
    }
}
