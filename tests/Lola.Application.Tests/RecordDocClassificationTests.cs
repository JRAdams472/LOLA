using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Events;
using Lola.Application.Tasks;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
using Lola.Domain.Applications;
using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Application.Tests;

/// M18 P2 — `RecordDocClassification`: the scan-drain persistence
/// step. The whole DocClassification lands on the doc row (repo owns
/// the vault: boundary); the event + audit spines carry kind,
/// confidence and finding NAMES only — extracted field values are
/// PII-class and never ride them (invariant 3).
public sealed class RecordDocClassificationTests
{
    private static readonly TenantRecord Tenant =
        new("t1", "t1", "T", "active", "x.db", "vault:v1:k",
            "2026-01-01T00:00:00Z");

    private static VaultDocRecord Doc(string id = "v-1") => new(
        id, "app-1", "w2", "w2-2025.pdf", "application/pdf",
        12_345, "ab12", "vault:v1:dek", "stub",
        $"quarantine/{id}", "quarantined", null, "officer-1",
        "2026-01-01", null, "rh-1");

    private static DocClassification W2Result() => new(
        "w2", 0.93,
        new Dictionary<string, string>
        { ["employer"] = "SEKRET-EMPLOYER-CORP", ["wages"] = "999999" },
        [new DocFinding("expired_doc", "doc date > 90d")]);

    private sealed class FakeVault : IVaultDocRepository
    {
        public VaultDocRecord? DocRow { get; set; }
        public Dictionary<string, VaultDocRecord> Extra { get; } = [];
        public string? SavedJson { get; private set; }
        public string? SavedUtc { get; private set; }
        public Task<VaultEnqueueResult> EnqueueAsync(VaultDocRecord doc,
            string outboxId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<VaultDocRecord?> GetAsync(string vaultDocId,
            CancellationToken ct = default)
            => Task.FromResult(vaultDocId == DocRow?.VaultDocId
                ? DocRow : Extra.GetValueOrDefault(vaultDocId));
        public Task<IReadOnlyList<VaultDocRecord>> ListAsync(string appId,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<VaultScanOutboxRow>> ListDueScansAsync(
            string nowUtc, int limit, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkScanningAsync(string outboxId,
            string nextRetryUtc, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task CompleteScanAsync(string outboxId, string vaultDocId,
            string cleanKey, string scannedUtc,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkInfectedAsync(string outboxId, string vaultDocId,
            string signature, string scannedUtc,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkUnavailableAsync(string outboxId,
            string vaultDocId, string error,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RescheduleScanAsync(string outboxId, long attempts,
            string nextRetryUtc, string error,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task SaveClassificationAsync(string vaultDocId,
            string classificationJson, string classifiedUtc,
            CancellationToken ct = default)
        {
            SavedJson = classificationJson;
            SavedUtc = classifiedUtc;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAudit : ITenantAuditLog
    {
        public List<(string Action, string? Reason)> Rows { get; } = [];
        public Task WriteAsync(string actor, string action,
            string entity, string? entityId,
            IReadOnlyList<string> fieldClasses, string? reason,
            CancellationToken ct = default)
        { Rows.Add((action, reason)); return Task.CompletedTask; }
        public Task<AuditVerifyResult> VerifyChainAsync(
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<AuditEvent>> QueryAsync(
            AuditFilter filter, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class ScriptedClassifier(
        DocClassification? result = null, Exception? throws = null)
        : IDocClassifier
    {
        public string Adapter => "scripted";
        public byte[]? Head { get; private set; }
        public Task<DocClassification> ClassifyAsync(VaultDocRecord doc,
            ReadOnlyMemory<byte> head, CancellationToken ct = default)
        {
            Head = head.ToArray();
            if (throws is not null) throw throws;
            return Task.FromResult(result!);
        }
    }

    private sealed class FakeApps(ApplicationRecord record)
        : IApplicationRepository
    {
        public Task<ApplicationRecord?> GetAsync(string id,
            CancellationToken ct = default)
            => Task.FromResult<ApplicationRecord?>(
                id == record.Id ? record : null);
        public Task<IReadOnlyList<ApplicationMeta>> ListAsync(
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ApplicationMeta>>(
                [new ApplicationMeta(record.Id, record.Status,
                    record.PipelineStep, null, 1, null, null, null,
                    "2026-01-05T10:00:00.000Z")]);
        public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
            string id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StepEventDto>>([]);
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
            string evt, string actor, string? reason = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationRetentionRow>>
            RetentionWindowsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<
                ApplicationRetentionRow>>([]);
    }

    private static ApplicationRecord App(string id = "app-1",
        string? propertyState = null)
    {
        var doc = ApplicationDocument.Empty();
        if (propertyState is not null)
            doc.Set("subject_property.state", propertyState);
        return new ApplicationRecord(id, "submitted", "POSIntake",
            "u-b1", 2, null, "2026-01-01T00:00:00Z", doc);
    }

    private sealed record Rig(
        FakeVault Vault, FakeAudit Audit, FakeEventLog Events,
        ScriptedClassifier Classifier, FakeConditionRepository Conds,
        FakeTaskRepository Tasks, DocClassifiedOutcome? Outcome);

    private static async Task<Rig> InvokeAsync(
        TenantRecord tenant, VaultDocRecord? doc,
        DocClassification result, string tenantScope,
        ApplicationRecord? app = null,
        FakeConditionRepository? conds = null)
    {
        var vault = new FakeVault { DocRow = doc };
        var audit = new FakeAudit();
        var events = new FakeEventLog();
        var classifier = new ScriptedClassifier(result);
        var conditions = conds ?? new FakeConditionRepository();
        var tasks = new FakeTaskRepository();
        var apps = new FakeApps(app ?? App());
        var eval = new TaskEvaluator(conditions, apps, tasks,
            TimeProvider.System);
        var outcome = default(DocClassifiedOutcome);
        using (TenantContext.Use(TenantScope.From(tenant)))
        {
            outcome = await RecordDocClassificationHandler.Handle(
                new RecordDocClassificationCommand(
                    tenantScope, doc?.VaultDocId ?? "v-x",
                    new byte[] { 1, 2, 3 }),
                classifier, vault, conditions, apps, audit, events,
                eval, TimeProvider.System, CancellationToken.None);
        }
        return new Rig(vault, audit, events, classifier, conditions,
            tasks, outcome);
    }

    [Fact]
    public async Task Persists_result_and_emits_metadata_only_spines()
    {
        var rig = await InvokeAsync(
            Tenant, Doc(), W2Result(), "t1");

        // Full result — extracted fields included — goes to the repo
        // (encrypted at rest); the spines see names only.
        var savedJson = rig.Vault.SavedJson.ShouldNotBeNull();
        savedJson.ShouldContain("SEKRET-EMPLOYER-CORP");
        rig.Vault.SavedUtc.ShouldNotBeNull();

        var evt = rig.Events.Drafts.Single(
            d => d.EventKind == "doc_classified");
        evt.AfterVal.ShouldBe("w2");
        evt.ApplicationId.ShouldBe("app-1");
        evt.EntityId.ShouldBe("v-1");

        var (action, reason) = rig.Audit.Rows.Single(
            r => r.Action == "doc_classified");
        var spine = reason.ShouldNotBeNull();
        spine.ShouldContain("kind=w2");
        spine.ShouldContain("confidence=0.93");
        spine.ShouldContain("expired_doc");

        // The extracted VALUES are provably absent from both spines.
        var detail = reason.ShouldNotBeNull();
        detail.ShouldNotContain("SEKRET-EMPLOYER-CORP");
        detail.ShouldNotContain("999999");
        evt.AfterVal!.ShouldNotContain("SEKRET-EMPLOYER-CORP");
        (evt.BeforeVal ?? "").ShouldNotContain("999999");
        rig.Outcome!.Kind.ShouldBe("w2");
        rig.Outcome.FindingCount.ShouldBe(1);
        // The finding in W2Result is `expired_doc` → a blocking
        // replace condition also opened (asserted in the P3 tests).
    }

    // ----- M18 P3 — auto-conditions from findings -----

    private static Condition Seed(FakeConditionRepository conds,
        string kind, string linkedEntity, string linkedEntityId,
        bool blocking = true)
    {
        var draft = new ConditionDraft("app-1", kind,
            $"seeded {kind}", Blocking: blocking,
            LinkedEntity: linkedEntity,
            LinkedEntityId: linkedEntityId, OpenedBy: "system");
        return conds.OpenAsync(draft).GetAwaiter().GetResult();
    }

    private static DocClassification Clean(string kind = "w2",
        IReadOnlyDictionary<string, string>? fields = null,
        params DocFinding[] findings) => new(kind, 0.93,
        fields ?? new Dictionary<string, string>(), findings);

    [Fact]
    public async Task Matching_kind_auto_closes_doc_required()
    {
        var conds = new FakeConditionRepository();
        Seed(conds, "doc_required", "doc_kind", "w2");
        var rig = await InvokeAsync(Tenant, Doc(),
            Clean("w2"), "t1", conds: conds);

        var cond = rig.Conds.Store.ShouldHaveSingleItem();
        cond.State.ShouldBe("closed");
        cond.CloseReason.ShouldNotBeNull().ShouldContain("v-1");
        rig.Events.Drafts.ShouldContain(
            d => d.EventKind == "condition_closed");
        rig.Audit.Rows.ShouldContain(
            r => r.Action == "condition_closed");
        // Convergent: the closed condition's queue task resolves.
        rig.Tasks.Resolves.ShouldContain(
            r => r.Entity == "condition"
                && r.EntityId == cond.ConditionId);
    }

    [Fact]
    public async Task Kind_mismatch_blocks_close_and_opens_replace()
    {
        var conds = new FakeConditionRepository();
        Seed(conds, "doc_required", "doc_kind", "w2");
        var rig = await InvokeAsync(Tenant, Doc(),
            Clean("w2", findings: [
                new DocFinding("kind_mismatch")]), "t1", conds: conds);

        // The requirement stays open — a flagged doc never satisfies.
        rig.Conds.Store.Count(c =>
            c.LinkedEntity == "doc_kind" && c.State == "open")
            .ShouldBe(1);
        var replace = rig.Conds.Store.Single(
            c => c.LinkedEntity == "vault_doc");
        replace.Kind.ShouldBe("doc_required");
        replace.Blocking.ShouldBeTrue();
        replace.LinkedEntityId.ShouldBe("v-1");
        replace.Title.ShouldContain("kind_mismatch");
        rig.Events.Drafts.ShouldContain(
            d => d.EventKind == "condition_opened");
    }

    [Fact]
    public async Task Expired_and_unreadable_open_blocking_replace()
    {
        var rig = await InvokeAsync(Tenant, Doc(),
            Clean("w2", findings: [
                new DocFinding("expired_doc"),
                new DocFinding("unreadable")]), "t1");

        var replace = rig.Conds.Store.Where(
            c => c.LinkedEntity == "vault_doc").ToList();
        replace.Count.ShouldBe(2);
        replace.ShouldAllBe(c => c.Blocking
            && c.Kind == "doc_required"
            && c.LinkedEntityId == "v-1");
        // Blocking conditions derive queue tasks in the eval pass.
        rig.Tasks.Store.Count(
            t => t.Kind == "condition_blocking").ShouldBe(2);
    }

    [Fact]
    public async Task Missing_field_opens_nonblocking_data_validation()
    {
        var rig = await InvokeAsync(Tenant, Doc(),
            Clean("w2", findings: [
                new DocFinding("missing_field")]), "t1");

        var cond = rig.Conds.Store.ShouldHaveSingleItem();
        cond.Kind.ShouldBe("data_validation");
        cond.Blocking.ShouldBeFalse();
        // Non-blocking → no queue task derived.
        rig.Tasks.Store.ShouldBeEmpty();
        // Advisory-only findings don't suppress auto-close.
    }

    [Fact]
    public async Task Distance_check_on_employer_property_divergence()
    {
        var fields = new Dictionary<string, string>
        { ["employer_address"] = "900 Pine St, Seattle, WA 98101" };
        var rig = await InvokeAsync(Tenant, Doc(),
            Clean("w2", fields), "t1", app: App(propertyState: "IL"));

        var cond = rig.Conds.Store.ShouldHaveSingleItem();
        cond.Kind.ShouldBe("distance_check");
        cond.Blocking.ShouldBeFalse();
        cond.LinkedEntity.ShouldBe("application");
        // State values never enter audit/event metadata.
        rig.Audit.Rows.ShouldNotContain(
            r => (r.Reason ?? "").Contains("WA")
                || (r.Reason ?? "").Contains("IL"));
    }

    [Fact]
    public async Task Distance_check_quiet_when_states_match()
    {
        var fields = new Dictionary<string, string>
        { ["employer_address"] = "1 Main St, Springfield, IL 62704" };
        var rig = await InvokeAsync(Tenant, Doc(),
            Clean("w2", fields), "t1", app: App(propertyState: "IL"));

        rig.Conds.Store.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_findings_emits_no_conditions()
    {
        var rig = await InvokeAsync(Tenant, Doc(), Clean("w2"), "t1");
        rig.Conds.Store.ShouldBeEmpty();
        rig.Events.Drafts.ShouldAllBe(
            d => d.EventKind == "doc_classified");
    }

    [Fact]
    public async Task Replacement_doc_closes_flagged_replace_condition()
    {
        var conds = new FakeConditionRepository();
        // The flagged doc v-old was declared w2 and got a replace
        // condition; the new upload v-1 detects w2 cleanly.
        Seed(conds, "doc_required", "vault_doc", "v-old");
        var vault = new FakeVault
        {
            DocRow = Doc("v-1"),
            Extra = { ["v-old"] = Doc("v-old") }
        };
        var audit = new FakeAudit();
        var events = new FakeEventLog();
        var tasks = new FakeTaskRepository();
        var apps = new FakeApps(App());
        var eval = new TaskEvaluator(conds, apps, tasks,
            TimeProvider.System);
        using var _ = TenantContext.Use(TenantScope.From(Tenant));
        await RecordDocClassificationHandler.Handle(
            new RecordDocClassificationCommand("t1", "v-1",
                new byte[] { 1 }),
            new ScriptedClassifier(Clean("w2")), vault, conds, apps,
            audit, events, eval, TimeProvider.System,
            CancellationToken.None);

        var cond = conds.Store.ShouldHaveSingleItem();
        cond.State.ShouldBe("closed");
        cond.CloseReason.ShouldNotBeNull().ShouldContain("v-1");
    }

    [Fact]
    public async Task Reclassify_converges_instead_of_accumulating()
    {
        var conds = new FakeConditionRepository();
        var result = Clean("w2", findings: [
            new DocFinding("expired_doc")]);
        await InvokeAsync(Tenant, Doc(), result, "t1", conds: conds);
        await InvokeAsync(Tenant, Doc(), result, "t1", conds: conds);

        conds.Store.Count(c => c.State == "open").ShouldBe(1);
    }

    [Fact]
    public async Task Missing_doc_throws_not_found()
    {
        var vault = new FakeVault { DocRow = null };
        var apps = new FakeApps(App());
        var conds = new FakeConditionRepository();
        using var _ = TenantContext.Use(TenantScope.From(Tenant));
        await Should.ThrowAsync<Lola.Application.Errors.DomainException>(
            () => RecordDocClassificationHandler.Handle(
                new RecordDocClassificationCommand("t1", "v-nope",
                    Array.Empty<byte>()),
                new ScriptedClassifier(W2Result()), vault, conds, apps,
                new FakeAudit(), new FakeEventLog(),
                new TaskEvaluator(conds, apps, new FakeTaskRepository(),
                    TimeProvider.System),
                TimeProvider.System, CancellationToken.None));
    }

    [Fact]
    public async Task Foreign_tenant_is_rejected()
    {
        var apps = new FakeApps(App());
        var conds = new FakeConditionRepository();
        using var _ = TenantContext.Use(TenantScope.From(Tenant));
        await Should.ThrowAsync<Lola.Application.Errors.DomainException>(
            () => RecordDocClassificationHandler.Handle(
                new RecordDocClassificationCommand("other-tenant",
                    "v-1", new byte[] { 1 }),
                new ScriptedClassifier(W2Result()),
                new FakeVault { DocRow = Doc() }, conds, apps,
                new FakeAudit(), new FakeEventLog(),
                new TaskEvaluator(conds, apps, new FakeTaskRepository(),
                    TimeProvider.System),
                TimeProvider.System, CancellationToken.None));
    }

    [Fact]
    public async Task Classifier_failure_propagates_for_drain_audit()
    {
        var vault = new FakeVault { DocRow = Doc() };
        var audit = new FakeAudit();
        var apps = new FakeApps(App());
        var conds = new FakeConditionRepository();
        using var _ = TenantContext.Use(TenantScope.From(Tenant));
        await Should.ThrowAsync<InvalidOperationException>(
            () => RecordDocClassificationHandler.Handle(
                new RecordDocClassificationCommand("t1", "v-1",
                    new byte[] { 1 }),
                new ScriptedClassifier(throws:
                    new InvalidOperationException("rules blew up")),
                vault, conds, apps, audit, new FakeEventLog(),
                new TaskEvaluator(conds, apps, new FakeTaskRepository(),
                    TimeProvider.System),
                TimeProvider.System, CancellationToken.None));
        // Nothing persisted, no spines — the drain owns the failure audit.
        vault.SavedJson.ShouldBeNull();
        audit.Rows.ShouldBeEmpty();
    }
}
