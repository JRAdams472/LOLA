using Lola.Application.Audit;
using Lola.Application.Events;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
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
        public string? SavedJson { get; private set; }
        public string? SavedUtc { get; private set; }
        public Task<VaultEnqueueResult> EnqueueAsync(VaultDocRecord doc,
            string outboxId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<VaultDocRecord?> GetAsync(string vaultDocId,
            CancellationToken ct = default) => Task.FromResult(DocRow);
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

    private static async Task<(FakeVault vault, FakeAudit audit,
        FakeEventLog events, ScriptedClassifier classifier,
        DocClassifiedOutcome outcome)> InvokeAsync(
        TenantRecord tenant, VaultDocRecord? doc,
        DocClassification result, string tenantScope)
    {
        var vault = new FakeVault { DocRow = doc };
        var audit = new FakeAudit();
        var events = new FakeEventLog();
        var classifier = new ScriptedClassifier(result);
        var outcome = default(DocClassifiedOutcome);
        using (TenantContext.Use(TenantScope.From(tenant)))
        {
            outcome = await RecordDocClassificationHandler.Handle(
                new RecordDocClassificationCommand(
                    tenantScope, doc?.VaultDocId ?? "v-x",
                    new byte[] { 1, 2, 3 }),
                classifier, vault, audit, events,
                TimeProvider.System, CancellationToken.None);
        }
        return (vault, audit, events, classifier, outcome);
    }

    [Fact]
    public async Task Persists_result_and_emits_metadata_only_spines()
    {
        var (vault, audit, events, _, outcome) = await InvokeAsync(
            Tenant, Doc(), W2Result(), "t1");

        // Full result — extracted fields included — goes to the repo
        // (encrypted at rest); the spines see names only.
        var savedJson = vault.SavedJson.ShouldNotBeNull();
        savedJson.ShouldContain("SEKRET-EMPLOYER-CORP");
        vault.SavedUtc.ShouldNotBeNull();

        var evt = events.Drafts.ShouldHaveSingleItem();
        evt.EventKind.ShouldBe("doc_classified");
        evt.AfterVal.ShouldBe("w2");
        evt.ApplicationId.ShouldBe("app-1");
        evt.EntityId.ShouldBe("v-1");

        var (action, reason) = audit.Rows.ShouldHaveSingleItem();
        action.ShouldBe("doc_classified");
        reason.ShouldContain("kind=w2");
        reason.ShouldContain("confidence=0.93");
        reason.ShouldContain("expired_doc");

        // The extracted VALUES are provably absent from both spines.
        var detail = reason.ShouldNotBeNull();
        detail.ShouldNotContain("SEKRET-EMPLOYER-CORP");
        detail.ShouldNotContain("999999");
        evt.AfterVal.ShouldNotContain("SEKRET-EMPLOYER-CORP");
        (evt.BeforeVal ?? "").ShouldNotContain("999999");
        outcome.Kind.ShouldBe("w2");
        outcome.FindingCount.ShouldBe(1);
    }

    [Fact]
    public async Task Missing_doc_throws_not_found()
    {
        var vault = new FakeVault { DocRow = null };
        using var _ = TenantContext.Use(TenantScope.From(Tenant));
        await Should.ThrowAsync<Lola.Application.Errors.DomainException>(
            () => RecordDocClassificationHandler.Handle(
                new RecordDocClassificationCommand("t1", "v-nope",
                    Array.Empty<byte>()),
                new ScriptedClassifier(W2Result()), vault,
                new FakeAudit(), new FakeEventLog(),
                TimeProvider.System, CancellationToken.None));
    }

    [Fact]
    public async Task Foreign_tenant_is_rejected()
    {
        using var _ = TenantContext.Use(TenantScope.From(Tenant));
        await Should.ThrowAsync<Lola.Application.Errors.DomainException>(
            () => RecordDocClassificationHandler.Handle(
                new RecordDocClassificationCommand("other-tenant",
                    "v-1", new byte[] { 1 }),
                new ScriptedClassifier(W2Result()),
                new FakeVault { DocRow = Doc() },
                new FakeAudit(), new FakeEventLog(),
                TimeProvider.System, CancellationToken.None));
    }

    [Fact]
    public async Task Classifier_failure_propagates_for_drain_audit()
    {
        var vault = new FakeVault { DocRow = Doc() };
        var audit = new FakeAudit();
        using var _ = TenantContext.Use(TenantScope.From(Tenant));
        await Should.ThrowAsync<InvalidOperationException>(
            () => RecordDocClassificationHandler.Handle(
                new RecordDocClassificationCommand("t1", "v-1",
                    new byte[] { 1 }),
                new ScriptedClassifier(throws:
                    new InvalidOperationException("rules blew up")),
                vault, audit, new FakeEventLog(),
                TimeProvider.System, CancellationToken.None));
        // Nothing persisted, no spines — the drain owns the failure audit.
        vault.SavedJson.ShouldBeNull();
        audit.Rows.ShouldBeEmpty();
    }
}
