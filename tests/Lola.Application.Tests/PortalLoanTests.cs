using System.Text.Json;
using System.Text.Json.Nodes;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Documents;
using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Application.Portal;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
using Lola.Domain.Applications;
using Lola.Domain.Classification;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-221 portal read surface: own-app scoping, masked doc, and the
/// borrower-vocabulary contract — no internal status/step vocabulary
/// or officer-only fields may appear in the serialized payload.
public sealed class PortalLoanTests
{
    private static IDisposable Scope(IReadOnlySet<PiiClass>? visible = null)
    {
        var tenant = TenantContext.Use(new TenantScope("t1", "x.db", "k"));
        var vis = VisibilityContext.Use(visible ?? new HashSet<PiiClass>());
        return new Both(tenant, vis);
    }

    private sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose() { b.Dispose(); a.Dispose(); }
    }

    private static ApplicationDocument DocWithPii()
        => ApplicationDocument.Parse("""
            { "party": [
                { "first_name": "Ada", "last_name": "Lovelace",
                  "ssn": "900123456",
                  "email": "ada@example.test" } ],
              "loan": { "amount": 250000 } }
            """);

    private static ApplicationRecord Record(
        string id = "app-1", string status = "draft",
        string? borrower = "u-b1", ApplicationDocument? doc = null) =>
        new(id, status, "POSIntake", borrower, 1, null, null,
            doc ?? DocWithPii());

    private sealed class FakeApps(ApplicationRecord record) : IApplicationRepository
    {
        public Task<ApplicationRecord?> GetAsync(string id,
            CancellationToken ct = default)
            => Task.FromResult<ApplicationRecord?>(id == record.Id ? record : null);
        public Task<IReadOnlyList<ApplicationMeta>> ListAsync(
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ApplicationMeta>>(
            [
                new ApplicationMeta(record.Id, record.Status,
                    record.PipelineStep, record.BorrowerUserId, 1,
                    null, "Ada Lovelace", null),
                new ApplicationMeta("app-other", "submitted", "Validate",
                    "u-b2", 1, null, "Someone Else", null),
            ]);
        public Task InsertAsync(string id, ApplicationDocument doc, string status,
            string? borrowerUserId, string writtenBy, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> WriteVersionAsync(string id, ApplicationDocument doc,
            string status, string writtenBy, bool submitted, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<(int Version, ApplicationDocument Doc)> PatchDraftAsync(
            string id, IReadOnlyDictionary<string, string> patch,
            string writtenBy, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdateStatusAsync(string id, string status,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task EnsureTridClockStartAsync(string id,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdatePipelineStepAsync(string id, string step,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordStepEventAsync(string id, string step, string event_,
            string actor, string? reason, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
            string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationRetentionRow>> RetentionWindowsAsync(
            CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeDocs : IDocumentRepository
    {
        public Task<IReadOnlyList<DocRecord>> ListDocsAsync(string appId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DocRecord>>(
            [
                new DocRecord("doc-1", appId, "le-package", "Initial LE",
                    "isppr", "req-9", "le.pdf", "completed", "vendor-error",
                    "officer-1", "2026-01-01", "2026-01-02", "hash-1"),
                new DocRecord("doc-2", appId, "le-package", "Revised LE",
                    "isppr", null, null, "generating", null,
                    "officer-1", "2026-01-03", null, "hash-2"),
            ]);
        public Task<DocEnqueueResult> EnqueueAsync(DocRecord doc, string outboxId,
            string kind, string payloadJson, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<DocRecord?> GetDocAsync(string docId,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DocOutboxRow>> ListDueOutboxAsync(
            string nowUtc, int limit, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkGeneratingAsync(string outboxId, string docId,
            string requestId, string nextRetryUtc, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task CompleteAsync(string outboxId, string docId,
            string requestId, string? filename, string completedUtc,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task RescheduleAsync(string outboxId, string docId, long attempts,
            string nextRetryUtc, string error, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task DeadLetterAsync(string outboxId, string docId, long attempts,
            string error, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeVault : IVaultDocRepository
    {
        public Task<IReadOnlyList<VaultDocRecord>> ListAsync(string appId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<VaultDocRecord>>(
            [
                new VaultDocRecord("v-1", appId, "paystub", "stub.pdf",
                    "application/pdf", 1234, "sha-1", "wrapped", "minio",
                    "clean/v-1", "clean", null, "u-b1",
                    "2026-01-04", "2026-01-04", "rh-1"),
                new VaultDocRecord("v-2", appId, "w2", "w2.pdf",
                    "application/pdf", 999, "sha-2", "wrapped", "minio",
                    "quar/v-2", "infected", "Win.Test.EICAR-1", "u-b1",
                    "2026-01-05", null, "rh-2"),
            ]);
        public Task<VaultEnqueueResult> EnqueueAsync(VaultDocRecord doc,
            string outboxId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<VaultDocRecord?> GetAsync(string vaultDocId,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<VaultScanOutboxRow>> ListDueScansAsync(
            string nowUtc, int limit, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkScanningAsync(string outboxId, string nextRetryUtc,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task CompleteScanAsync(string outboxId, string vaultDocId,
            string cleanKey, string scannedUtc, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkInfectedAsync(string outboxId, string vaultDocId,
            string signature, string scannedUtc, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkUnavailableAsync(string outboxId, string vaultDocId,
            string error, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RescheduleScanAsync(string outboxId, long attempts,
            string nextRetryUtc, string error, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeAudit : ITenantAuditLog
    {
        public Task WriteAsync(string actor, string action, string entity,
            string? entityId, IReadOnlyList<string> fieldClasses,
            string? reason, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<AuditVerifyResult> VerifyChainAsync(
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditFilter filter,
            CancellationToken ct = default) => throw new NotImplementedException();
    }

    [Theory]
    [InlineData("draft", "Application in progress", 0)]
    [InlineData("submitted", "Under review", 1)]
    [InlineData("in_review", "Under review", 1)]
    [InlineData("decisioned", "Decision made", 2)]
    [InlineData("POSIntake", "Your application", 0)]  // internal step → fallback
    [InlineData("PPE", "Your application", 0)]        // never echoed
    [InlineData("nonsense", "Your application", 0)]
    public void StatusMap_covers_every_status_and_falls_back_safely(
        string status, string label, int stage)
    {
        BorrowerStatusMap.Map(status).ShouldBe((label, stage));
    }

    [Fact]
    public async Task PortalLoan_returns_masked_doc_and_borrower_vocabulary()
    {
        using var _ = Scope();
        var dto = await GetPortalLoanHandler.Handle(
            new GetPortalLoanQuery("t1", "app-1", "u-b1"),
            new FakeApps(Record()), new FakeDocs(), new FakeVault(),
            new FakeAudit(), CancellationToken.None);

        dto.ShouldNotBeNull();
        dto.Id.ShouldBe("app-1");
        dto.StatusLabel.ShouldBe("Application in progress");
        dto.StatusStage.ShouldBe(0);
        dto.StatusStages.Count.ShouldBe(3);
        // Masked-confirm: borrower scope has zero plaintext grants.
        dto.Doc["party"]![0]!["ssn"]!.GetValue<string>()
            .ShouldStartWith("•••");
        dto.Doc["party"]![0]!["ssn"]!.GetValue<string>()
            .ShouldEndWith("3456");
        dto.Doc["party"]![0]!["email"]!.GetValue<string>()
            .ShouldStartWith("•••");
        // Friendly upload/doc statuses — never raw quarantine vocabulary.
        dto.Uploads[0].StatusLabel.ShouldBe("Received");
        dto.Uploads[1].StatusLabel.ShouldContain("couldn't accept");
        dto.Documents[0].Ready.ShouldBeTrue();
        dto.Documents[1].Ready.ShouldBeFalse();
        dto.Documents[0].KindLabel.ShouldBe("Loan Estimate package");
    }

    /// The ticket's key acceptance, asserted on the serialized payload —
    /// not "the record happened to lack a field" but the wire bytes.
    [Fact]
    public async Task PortalLoan_serialized_payload_has_no_internal_fields()
    {
        using var _ = Scope();
        var dto = await GetPortalLoanHandler.Handle(
            new GetPortalLoanQuery("t1", "app-1", "u-b1"),
            new FakeApps(Record(status: "submitted")), new FakeDocs(),
            new FakeVault(), new FakeAudit(), CancellationToken.None);

        var json = JsonSerializer.Serialize(dto,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });
        foreach (var forbidden in new[]
        {
            "pipelineStep", "pipeline", "currentVersion", "borrowerUserId",
            "trid", "lock", "submittedUtc", "sha256", "s3Key", "adapter",
            "actor", "requestHash", "requestId", "wrappedDek", "error",
            "\"status\"", "POSIntake", "PPE", "quarantined", "infected",
            "vendor-error", "EICAR", "in_review",
        })
            json.ShouldNotContain(forbidden);
    }

    [Fact]
    public async Task PortalLoan_denies_other_borrowers_app()
    {
        using var _ = Scope();
        await Should.ThrowAsync<DomainException>(async () =>
            await GetPortalLoanHandler.Handle(
                new GetPortalLoanQuery("t1", "app-1", "u-b2"),
                new FakeApps(Record()), new FakeDocs(), new FakeVault(),
                new FakeAudit(), CancellationToken.None));
    }

    [Fact]
    public async Task PortalLoans_lists_only_own_apps_with_borrower_labels()
    {
        using var _ = Scope();
        var metas = await ListPortalLoansHandler.Handle(
            new ListPortalLoansQuery("t1", "u-b1"),
            new FakeApps(Record(status: "submitted")), CancellationToken.None);

        metas.Count.ShouldBe(1);                       // app-other (u-b2) hidden
        metas[0].Id.ShouldBe("app-1");
        metas[0].StatusLabel.ShouldBe("Under review"); // not "submitted"
        metas[0].StatusStage.ShouldBe(1);
    }
}
