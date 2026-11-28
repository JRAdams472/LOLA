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

/// M24 P2 (MPE-313) — HMDA wiring: assemble pipeline, export artifact,
/// snapshot freeze, and the negative-case matrix.
public sealed class HmdaWiringTests
{

    private sealed class FakeAusStore : Lola.Application.Aus.IAusResultsStore
    {
        public List<Lola.Application.Aus.AusResultRow> Results { get; } = [];

        public Task<Lola.Application.Aus.AusResultRow> AddResultAsync(
            string appId, string vendor, string? orderId, string? caseId,
            string recommendation, string? riskClass, string? responseXml,
            string responseHash, int findingsCount, string actor,
            CancellationToken ct)
        {
            var row = new Lola.Application.Aus.AusResultRow(
                $"aus-{Results.Count + 1}", appId, vendor, orderId, caseId,
                recommendation, riskClass, findingsCount,
                "2026-01-01T00:00:00.000Z", actor);
            Results.Add(row);
            return Task.FromResult(row);
        }

        public Task AddFindingAsync(string resultId, string vendorCode,
            string category, string severity, string message,
            string? conditionId, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<Lola.Application.Aus.AusResultRow>>
            ListResultsAsync(string appId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Lola.Application.Aus.AusResultRow>>(
                Results.Where(r => r.ApplicationId == appId).ToList());

        public Task<IReadOnlyList<Lola.Application.Aus.AusFindingRow>>
            ListFindingsAsync(string resultId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Lola.Application.Aus.AusFindingRow>>(
                new List<Lola.Application.Aus.AusFindingRow>());
    }

    // -- fakes ------------------------------------------------------

    private sealed class FakeHmda : IHmdaRepository
    {
        public List<LarRowRecord> Rows { get; } = [];
        public List<EditRunRecord> Edits { get; } = [];
        public List<LarSnapshotRecord> Snapshots { get; } = [];

        private static string Stamp(int n)
            => $"2026-01-01T00:00:{n % 60:00}.000Z";

        public Task<LarRowRecord> AddLarRowAsync(string appId,
            int reportingYear, string uli, string rowJson,
            string derivedJson, string actor, CancellationToken ct)
        {
            var r = new LarRowRecord($"lar-{Rows.Count + 1}", appId,
                reportingYear, uli, rowJson, derivedJson, actor,
                Stamp(Rows.Count));
            Rows.Add(r);
            return Task.FromResult(r);
        }

        public Task<IReadOnlyList<LarRowRecord>> ListLarRowsAsync(
            string appId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LarRowRecord>>(
                Rows.Where(r => r.ApplicationId == appId)
                    .OrderByDescending(r => r.CreatedUtc).ToList());

        public Task<IReadOnlyList<LarRowRecord>>
            ListLarRowsByYearAsync(int reportingYear,
                CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LarRowRecord>>(
                Rows.Where(r => r.ReportingYear == reportingYear)
                    .OrderByDescending(r => r.CreatedUtc).ToList());

        public Task AddEditRunAsync(string larRowId, string tier,
            string editId, string result, string detail,
            CancellationToken ct)
        {
            Edits.Add(new EditRunRecord($"edit-{Edits.Count + 1}",
                larRowId, tier, editId, result, detail,
                Stamp(Edits.Count)));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EditRunRecord>> ListEditRunsAsync(
            string larRowId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EditRunRecord>>(
                Edits.Where(e => e.LarRowId == larRowId).ToList());

        public Task<LarSnapshotRecord> AddSnapshotAsync(
            int reportingYear, IReadOnlyList<string> larRowIds,
            string actor, CancellationToken ct)
        {
            var s = new LarSnapshotRecord($"snap-{Snapshots.Count + 1}",
                reportingYear, larRowIds.Count, actor,
                Stamp(60 + Snapshots.Count));
            Snapshots.Add(s);
            return Task.FromResult(s);
        }

        public Task<IReadOnlyList<LarSnapshotRecord>>
            ListSnapshotsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LarSnapshotRecord>>(
                Snapshots);
    }

    private sealed class FakeAtr : IAtrQmRepository
    {
        public List<AtrResultRecord> Results { get; } = [];
        public Task<AtrResultRecord> AddResultAsync(string appId,
            string? evalId, decimal? dti, decimal? apr, decimal? apor,
            string verdictsJson, string actor, CancellationToken ct)
        {
            var r = new AtrResultRecord($"atr-{Results.Count + 1}",
                appId, evalId, (double?)dti, (double?)apr, (double?)apor,
                verdictsJson, actor, "2026-01-01T00:00:00.000Z");
            Results.Add(r);
            return Task.FromResult(r);
        }
        public Task<IReadOnlyList<AtrResultRecord>> ListResultsAsync(
            string appId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AtrResultRecord>>(
                Results.Where(r => r.ApplicationId == appId)
                    .OrderByDescending(r => r.CreatedUtc).ToList());
        public Task<AporRateRecord?> GetAporAsync(CancellationToken ct)
            => Task.FromResult<AporRateRecord?>(null);
        public Task<IReadOnlyDictionary<string, PfClassRecord>>
            GetPfClassesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string,
                PfClassRecord>>(new Dictionary<string, PfClassRecord>());
        public Task<StateHighCostRecord?> GetStateRuleAsync(
            string stateCode, CancellationToken ct)
            => Task.FromResult<StateHighCostRecord?>(null);
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

    // -- AssembleLar --------------------------------------------------

    [Fact]
    public async Task Assemble_writes_row_edits_and_event()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };

        var dto = await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);

        dto.ReportingYear.ShouldBe(2026);
        hmda.Rows.ShouldHaveSingleItem();
        // exactly one edit run row per battery edit — reconstruction
        hmda.Edits.Count.ShouldBe(Lola.Domain.Hmda.EditChecks.All.Count);
        var draft = events.Drafts.ShouldHaveSingleItem();
        draft.EventKind.ShouldBe(LoanEventKind.HmdaLar);
        draft.AfterVal!.ShouldContain("year:2026");
        // invariant 3 — no field values in the event summary
        draft.AfterVal!.ShouldNotContain("300000");
        draft.AfterVal!.ShouldNotContain("white");
    }

    [Fact]
    public async Task Assemble_uli_carries_valid_check_digit()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };

        var dto = await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);

        dto.Uli.ShouldStartWith("10BX939C5543TQA1144M");
        Lola.Domain.Hmda.Uli.Validate(dto.Uli).ShouldBeTrue();
    }

    [Fact]
    public async Task Reassemble_inserts_append_only_never_updates()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };

        await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);
        await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);

        hmda.Rows.Count.ShouldBe(2);
        hmda.Rows[0].LarRowId.ShouldNotBe(hmda.Rows[1].LarRowId);
    }

    [Fact]
    public async Task Missing_atr_evaluation_assembles_na_never_500()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();  // empty — no evaluation
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };

        var dto = await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);

        dto.Fields["rate_spread"].ShouldBe("NA");
        dto.Fields["hoepa_status"].ShouldBe("NA");
    }

    [Fact]
    public async Task Atr_evaluation_supplies_rate_spread_inputs()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();
        await atr.AddResultAsync("app-1", null, 36.5m, 6.75m, 5.25m,
            """[{"test":"hoepa","verdict":"pass","detail":""}]""",
            "officer-1", default);
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };

        var dto = await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);

        dto.Fields["rate_spread"].ShouldBe("1.50");
        dto.Fields["hoepa_status"].ShouldBe("2");
        dto.Fields["debt_to_income_ratio"].ShouldBe("36.5");
    }

    [Fact]
    public async Task Hoepa_fail_verdict_marks_high_cost()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();
        await atr.AddResultAsync("app-1", null, 50m, 9m, 5m,
            """[{"test":"hoepa","verdict":"fail","detail":""}]""",
            "officer-1", default);
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };

        var dto = await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);

        dto.Fields["hoepa_status"].ShouldBe("1");  // high-cost
    }

    [Fact]
    public async Task Non_staff_cannot_assemble()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };

        await Should.ThrowAsync<DomainException>(() =>
            AssembleLarHandler.Handle(
                new AssembleLarCommand("t1", "app-1", "borrower-1",
                    ActorScope.Borrower),
                apps, atr, hmda, events, Opts, new FakeAusStore(), default));
        hmda.Rows.ShouldBeEmpty();
    }

    // -- GetApplicationHmda -------------------------------------------

    [Fact]
    public async Task Report_groups_edits_with_lineage()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };

        await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);
        await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);

        var report = await GetApplicationHmdaHandler.Handle(
            new GetApplicationHmdaQuery("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, hmda, default);

        report.Latest.ShouldNotBeNull();
        report.Lineage.Count.ShouldBe(2);
        report.Latest.Edits.ShouldNotBeEmpty();
        report.Latest.Edits.ShouldContain(
            e => e.Tier == "validity" && e.EditId == "V608");
        report.Figures.ActionTaken.ShouldBe("1");  // originated
        report.Figures.RateSpread.ShouldBeNull();  // NA — no atr eval
    }

    [Fact]
    public async Task Empty_app_reports_null_latest()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var apps = new FakeApps { Doc = HmdaDoc() };

        var report = await GetApplicationHmdaHandler.Handle(
            new GetApplicationHmdaQuery("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, hmda, default);

        report.Latest.ShouldBeNull();
        report.Lineage.ShouldBeEmpty();
    }

    // -- ExportLar ----------------------------------------------------

    private static async Task SeedTwoApps(FakeHmda hmda, FakeAtr atr,
        FakeEventLog events, FakeApps apps)
    {
        await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);
        await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-2", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);
    }

    [Fact]
    public async Task Export_produces_pipe_delimited_rows_plus_ts_header()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };
        await SeedTwoApps(hmda, atr, events, apps);
        events.Drafts.Clear();

        var result = await ExportLarHandler.Handle(
            new ExportLarCommand("t1", 2026, "officer-1",
                ActorScope.Staff),
            hmda, events, Opts, default);

        result.RowCount.ShouldBe(2);
        var text = System.Text.Encoding.UTF8.GetString(result.Content);
        var lines = text.Split('\n',
            StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(3);  // TS + 2 LAR
        lines[0].ShouldStartWith("1|");  // transmittal record id
        lines[1].ShouldStartWith("2|");  // LAR record id
        // pipe count matches the canonical column count
        lines[1].Split('|').Length.ShouldBe(
            Lola.Domain.Hmda.LarSchema.ExportColumns.Count);
        lines[1].ShouldContain("10BX939C5543TQA1144M");
        // invariant 2 — hash-chained export events, one per application
        events.Drafts.Count.ShouldBe(2);
        events.Drafts.ShouldAllBe(
            d => d.EventKind == LoanEventKind.HmdaExport
                 && d.Actor == "officer-1");
    }

    [Fact]
    public async Task Export_dedupes_reassembled_apps_to_latest()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };
        // same app assembled twice — export counts it once
        await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);
        await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);

        var result = await ExportLarHandler.Handle(
            new ExportLarCommand("t1", 2026, "officer-1",
                ActorScope.Staff),
            hmda, events, Opts, default);

        result.RowCount.ShouldBe(1);
    }

    [Fact]
    public async Task Export_zero_rows_conflicts_never_empty_artifact()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var events = new FakeEventLog();

        await Should.ThrowAsync<DomainException>(() =>
            ExportLarHandler.Handle(
                new ExportLarCommand("t1", 2026, "officer-1",
                    ActorScope.Staff),
                hmda, events, Opts, default));
        events.Drafts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Non_staff_cannot_export()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var events = new FakeEventLog();

        await Should.ThrowAsync<DomainException>(() =>
            ExportLarHandler.Handle(
                new ExportLarCommand("t1", 2026, "borrower-1",
                    ActorScope.Borrower),
                hmda, events, Opts, default));
    }

    // -- FreezeLarSnapshot --------------------------------------------

    [Fact]
    public async Task Freeze_appends_immutable_snapshot_rows()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };
        await SeedTwoApps(hmda, atr, events, apps);

        var snap = await FreezeLarSnapshotHandler.Handle(
            new FreezeLarSnapshotCommand("t1", 2026, "officer-1",
                ActorScope.Staff),
            hmda, default);

        snap.RowCount.ShouldBe(2);
        hmda.Snapshots.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Refreeze_appends_new_snapshot_never_rewrites()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();
        var atr = new FakeAtr();
        var events = new FakeEventLog();
        var apps = new FakeApps { Doc = HmdaDoc() };
        await SeedTwoApps(hmda, atr, events, apps);

        var first = await FreezeLarSnapshotHandler.Handle(
            new FreezeLarSnapshotCommand("t1", 2026, "officer-1",
                ActorScope.Staff), hmda, default);
        var second = await FreezeLarSnapshotHandler.Handle(
            new FreezeLarSnapshotCommand("t1", 2026, "officer-1",
                ActorScope.Staff), hmda, default);

        hmda.Snapshots.Count.ShouldBe(2);
        first.SnapshotId.ShouldNotBe(second.SnapshotId);
        // the original freeze keeps its shape — row count unchanged
        first.RowCount.ShouldBe(2);
    }

    [Fact]
    public async Task Freeze_zero_rows_conflicts()
    {
        using var _ = Scope();
        var hmda = new FakeHmda();

        await Should.ThrowAsync<DomainException>(() =>
            FreezeLarSnapshotHandler.Handle(
                new FreezeLarSnapshotCommand("t1", 2026, "officer-1",
                    ActorScope.Staff), hmda, default));
    }

    [Fact]
    public async Task Assemble_populates_aus_columns_when_result_exists()
    {
        var apps = new FakeApps();
        apps.Doc = HmdaDoc();
        var atr = new FakeAtr();
        var hmda = new FakeHmda();
        var events = new FakeEventLog();
        var aus = new FakeAusStore();
        await aus.AddResultAsync("app-1", "du", null, "case-1",
            "approve-eligible", "approve", null, "hash", 0, "sys",
            CancellationToken.None);
        using var _ = Scope();

        var dto = await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, aus, default);

        var row = hmda.Rows.ShouldHaveSingleItem();
        row.RowJson.ShouldContain("\"aus_1\":\"1\"");
        row.RowJson.ShouldContain("\"aus_result_1\":\"1\"");
    }

    [Fact]
    public async Task Assemble_without_aus_results_keeps_na()
    {
        var apps = new FakeApps();
        apps.Doc = HmdaDoc();
        var atr = new FakeAtr();
        var hmda = new FakeHmda();
        var events = new FakeEventLog();
        using var _ = Scope();

        var dto = await AssembleLarHandler.Handle(
            new AssembleLarCommand("t1", "app-1", "officer-1",
                ActorScope.Staff),
            apps, atr, hmda, events, Opts, new FakeAusStore(), default);

        var row = hmda.Rows.ShouldHaveSingleItem();
        row.RowJson.ShouldContain("\"aus_1\":\"NA\"");
    }
}
