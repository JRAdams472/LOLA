using Lola.Api.Vault;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Events;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tasks;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;

namespace Lola.Api.Tests;

/// M18 P2 — `VaultScanProcessor` tees a bounded plaintext head to
/// `IDocClassifier` after a Clean verdict and invokes
/// `RecordDocClassification` through the real Wolverine mediator
/// BEFORE promotion: the result lands on the doc row and
/// `doc_classified` hits the spines. A classifier exception audits
/// `doc_classify_failed` and the doc still promotes — AV is the only
/// upload gate. Infected/unavailable never reach the classifier.
public sealed class VaultScanProcessorTests : IAsyncLifetime
{
    private static readonly TenantRecord Tenant =
        new("t1", "t1", "T", "active", "x.db", "vault:v1:k",
            "2026-01-01T00:00:00Z");

    private IHost _host = null!;
    private readonly FakeVaultDocRepository _repo = new();
    private readonly FakeObjectVault _store = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeEvents _events = new();
    private readonly ScriptedClassifier _classifier = new();
    private FakeConditions _conditions = null!;
    private FakeApps _apps = null!;
    private FakeTasks _tasks = null!;
    private ScanVerdict _verdict = new(ScanStatus.Clean, null);
    private VaultCipher _cipher = null!;

    private static ApplicationRecord App() => new(
        "app-1", "submitted", "POSIntake", "u-b1", 2, null,
        "2026-01-01T00:00:00Z", AppDocWithProperty());

    public async Task InitializeAsync()
    {
        _cipher = new VaultCipher(new FakeFieldCipher());
        var builder = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
                opts.Discovery.IncludeAssembly(
                    typeof(RecordDocClassificationCommand).Assembly));
        _conditions = new FakeConditions();
        _apps = new FakeApps(App());
        _tasks = new FakeTasks();
        builder.ConfigureServices(s => s
            .AddSingleton<IVaultDocRepository>(_repo)
            .AddSingleton<IObjectVault>(_store)
            .AddSingleton<IVirusScanner>(new ScriptedScanner(
                () => _verdict))
            .AddSingleton(_cipher)
            .AddSingleton<ITenantAuditLog>(_audit)
            .AddSingleton<ILoanEventLog>(_events)
            .AddSingleton<IDocClassifier>(_classifier)
            .AddSingleton<IConditionRepository>(_conditions)
            .AddSingleton<IApplicationRepository>(_apps)
            .AddSingleton<ITaskRepository>(_tasks)
            .AddSingleton(new TaskEvaluator(_conditions, _apps,
                _tasks, TimeProvider.System))
            .AddSingleton(TimeProvider.System));
        _host = builder.Build();
        await _host.StartAsync();
    }

    public async Task DisposeAsync() => await _host.StopAsync();

    private async Task<VaultDocRecord> EnqueueAsync(byte[] plaintext,
        string docId = "v-1", string declared = "w2")
    {
        // Seal through the real envelope cipher — the drain opens the
        // same ciphertext back to plaintext mid-scan.
        var sealing = _cipher.Seal(new MemoryStream(plaintext),
            VaultScanProcessorTests.MaxDoc);
        using var cipherMs = new MemoryStream();
        await sealing.CopyToAsync(cipherMs);
        _store.Objects[$"quarantine/{docId}"] = cipherMs.ToArray();

        var doc = new VaultDocRecord(docId, "app-1", declared,
            "doc.pdf", "application/pdf", plaintext.Length,
            sealing.Sha256Hex, sealing.WrappedDek, "stub",
            $"quarantine/{docId}", "quarantined", null, "officer-1",
            "2026-01-01", null, $"rh-{docId}");
        using var _ = TenantContext.Use(TenantScope.From(Tenant));
        await _repo.EnqueueAsync(doc, $"vso-{docId}");
        return doc;
    }

    private VaultScanProcessor Processor() =>
        new(_host.Services, new FakeRegistry(), TimeProvider.System);

    [Fact]
    public async Task Clean_classifies_then_promotes_with_spines()
    {
        _classifier.Result = new DocClassification("w2", 0.93,
            new Dictionary<string, string>
            { ["employer"] = "SEKRET-EMPLOYER-CORP" },
            [new DocFinding("expired_doc")]);
        var plaintext = System.Text.Encoding.ASCII
            .GetBytes("W-2 wage statement test head");
        var doc = await EnqueueAsync(plaintext);

        var drained = await Processor().DrainTenantAsync(Tenant);
        drained.ShouldBe(1);

        // Classification BEFORE promote — the row carries the result.
        var saved = _repo.Docs[doc.VaultDocId];
        saved.Status.ShouldBe("clean");
        saved.S3Key.ShouldBe($"clean/{doc.VaultDocId}");
        saved.ClassificationJson.ShouldNotBeNull();
        saved.ClassificationJson.ShouldContain("SEKRET-EMPLOYER-CORP");
        saved.ClassifiedUtc.ShouldNotBeNull();
        _store.Objects.ShouldContainKey($"clean/{doc.VaultDocId}");

        // Metadata-only spines — the field value never leaves the row.
        var evt = _events.Drafts.Single(
            d => d.EventKind == "doc_classified");
        evt.AfterVal.ShouldBe("w2");
        evt.AfterVal!.ShouldNotContain("SEKRET-EMPLOYER-CORP");
        // P3: the scripted `expired_doc` finding opened a blocking
        // replace-doc condition in the same drain pass.
        _events.Drafts.ShouldContain(
            d => d.EventKind == "condition_opened");
        _conditions.Store.ShouldHaveSingleItem()
            .Blocking.ShouldBeTrue();
        var (action, reason) = _audit.Rows
            .Single(r => r.Action == "doc_classified");
        var detail = reason.ShouldNotBeNull();
        detail.ShouldContain("kind=w2");
        detail.ShouldNotContain("SEKRET-EMPLOYER-CORP");
        _audit.Rows.ShouldContain(r => r.Action == "vault_scan_clean");
    }

    [Fact]
    public async Task Classifier_exception_audits_and_still_promotes()
    {
        _classifier.Throws = new InvalidOperationException(
            "rule table blew up");
        await EnqueueAsync([1, 2, 3]);

        var drained = await Processor().DrainTenantAsync(Tenant);
        drained.ShouldBe(1);

        var saved = _repo.Docs["v-1"];
        saved.Status.ShouldBe("clean");
        saved.S3Key.ShouldBe("clean/v-1");
        saved.ClassificationJson.ShouldBeNull();
        _store.Objects.ShouldContainKey("clean/v-1");
        _audit.Rows.ShouldContain(
            r => r.Action == "doc_classify_failed");
        _events.Drafts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Infected_never_reaches_the_classifier()
    {
        _verdict = new ScanVerdict(ScanStatus.Infected,
            "Eicar-Test-Signature");
        await EnqueueAsync([9, 9, 9]);

        await Processor().DrainTenantAsync(Tenant);
        _repo.Docs["v-1"].Status.ShouldBe("infected");
        _repo.Docs["v-1"].ClassificationJson.ShouldBeNull();
        _classifier.Invocations.ShouldBe(0);
    }

    [Fact]
    public async Task Head_is_bounded_at_two_mib()
    {
        var plaintext = new byte[(2 * 1024 * 1024) + 777];
        new Random(42).NextBytes(plaintext);
        await EnqueueAsync(plaintext, "v-big");
        _classifier.Result = new DocClassification("misc", 0.1,
            new Dictionary<string, string>(), []);

        await Processor().DrainTenantAsync(Tenant);
        _classifier.Invocations.ShouldBe(1);
        _classifier.LastHead!.Length.ShouldBe(2 * 1024 * 1024);
        _repo.Docs["v-big"].Status.ShouldBe("clean");
    }

    private const int MaxDoc = 25 * 1024 * 1024;

    private sealed class FakeFieldCipher : IFieldCipher
    {
        public Task<string> EncryptAsync(string plaintext,
            CancellationToken ct = default)
            => Task.FromResult($"vault:v1:{plaintext}");
        public Task<IReadOnlyList<string>> EncryptAsync(
            IReadOnlyList<string> plaintexts,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(
                plaintexts.Select(p => $"vault:v1:{p}").ToList());
        public Task<string> DecryptAsync(string ciphertext,
            CancellationToken ct = default)
            => Task.FromResult(ciphertext["vault:v1:".Length..]);
        public Task<IReadOnlyList<string>> DecryptAsync(
            IReadOnlyList<string> ciphertexts,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(ciphertexts
                .Select(c => c["vault:v1:".Length..]).ToList());
    }

    private sealed class FakeObjectVault : IObjectVault
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public string Adapter => "stub";
        public async Task PutAsync(string key, Stream content,
            CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            Objects[key] = ms.ToArray();
        }
        public Task<Stream> GetAsync(string key,
            CancellationToken ct = default)
            => Task.FromResult<Stream>(
                new MemoryStream(Objects[key]));
        public Task CopyAsync(string sourceKey, string destKey,
            CancellationToken ct = default)
        {
            Objects[destKey] = Objects[sourceKey];
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key,
            CancellationToken ct = default)
        { Objects.Remove(key); return Task.CompletedTask; }
        public Task PurgeTenantAsync(CancellationToken ct = default)
        { Objects.Clear(); return Task.CompletedTask; }
    }

    private sealed class ScriptedScanner(Func<ScanVerdict> next)
        : IVirusScanner
    {
        public string Adapter => "scripted";
        public async Task<ScanVerdict> ScanAsync(Stream content,
            CancellationToken ct = default)
        {
            // Drain the stream like a real INSTREAM read — the head
            // capture only sees what actually flows through.
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            return next();
        }
    }

    private sealed class ScriptedClassifier : IDocClassifier
    {
        public DocClassification? Result { get; set; }
        public Exception? Throws { get; set; }
        public int Invocations { get; private set; }
        public byte[]? LastHead { get; private set; }
        public string Adapter => "scripted";
        public Task<DocClassification> ClassifyAsync(VaultDocRecord doc,
            ReadOnlyMemory<byte> head, CancellationToken ct = default)
        {
            Invocations++;
            LastHead = head.ToArray();
            if (Throws is not null) throw Throws;
            return Task.FromResult(Result
                ?? new DocClassification("misc", 0.5,
                    new Dictionary<string, string>(), []));
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
            => Task.FromResult(new AuditVerifyResult(true, null, 0));
        public Task<IReadOnlyList<AuditEvent>> QueryAsync(
            AuditFilter filter, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditEvent>>([]);
    }

    private static Lola.Domain.Applications.ApplicationDocument
        AppDocWithProperty()
    {
        var doc = Lola.Domain.Applications.ApplicationDocument.Empty();
        doc.Set("subject_property.state", "IL");
        return doc;
    }

    private sealed class FakeConditions : IConditionRepository
    {
        public List<Condition> Store { get; } = [];
        private int _n;
        public Task<Condition> OpenAsync(ConditionDraft draft,
            CancellationToken ct = default)
        {
            var c = new Condition($"cond-{++_n}", draft.ApplicationId,
                draft.Kind, ConditionState.Open, draft.Blocking,
                draft.LinkedEntity, draft.LinkedEntityId, draft.Title,
                draft.Detail, draft.OpenedBy,
                "2026-01-01T00:00:00.000Z", null, null, null);
            Store.Add(c);
            return Task.FromResult(c);
        }
        public Task<Condition?> GetAsync(string conditionId,
            CancellationToken ct = default)
            => Task.FromResult(Store.FirstOrDefault(
                c => c.ConditionId == conditionId));
        public Task<IReadOnlyList<Condition>> ListByApplicationAsync(
            string applicationId, string? state = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Condition>>(Store
                .Where(c => c.ApplicationId == applicationId
                    && (state is null || c.State == state))
                .ToList());
        public Task<Condition> TransitionAsync(string conditionId,
            string toState, string? reason,
            CancellationToken ct = default)
        {
            var c = Store.First(x => x.ConditionId == conditionId);
            var i = Store.IndexOf(c);
            Store[i] = c with
            {
                State = toState,
                CloseReason = reason
            };
            return Task.FromResult(Store[i]);
        }
    }

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
        public Task<IReadOnlyList<ApplicationMeta>> ListAsync(
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ApplicationMeta>>(
                [new ApplicationMeta(record.Id, record.Status,
                    record.PipelineStep, null, 1, null, null, null,
                    "2026-01-05T10:00:00.000Z")]);
        public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
            string id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StepEventDto>>([]);
        public Task InsertAsync(string id,
            Lola.Domain.Applications.ApplicationDocument doc,
            string status, string? borrowerUserId, string writtenBy,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> WriteVersionAsync(string id,
            Lola.Domain.Applications.ApplicationDocument doc,
            string status, string writtenBy, bool submitted,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<(int Version,
            Lola.Domain.Applications.ApplicationDocument Doc)>
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

    private sealed class FakeTasks : ITaskRepository
    {
        public List<QueueTask> Store { get; } = [];
        private long _seq;
        public Task<QueueTask> UpsertAsync(TaskDraft draft,
            CancellationToken ct = default)
        {
            var key = draft.BlockingEntityId ?? "";
            var live = Store.FirstOrDefault(t =>
                t.TenantId == draft.TenantId
                && t.ApplicationId == draft.ApplicationId
                && t.Kind == draft.Kind
                && (t.BlockingEntityId ?? "") == key
                && t.State is TaskState.Open or TaskState.Claimed);
            if (live is not null)
            {
                var i = Store.IndexOf(live);
                Store[i] = live with
                {
                    Title = draft.Title,
                    Priority = draft.Priority,
                    SkillTags = draft.SkillTags ?? [],
                    DueUtc = draft.DueUtc,
                    BlockingEntity = draft.BlockingEntity
                };
                return Task.FromResult(Store[i]);
            }
            var task = new QueueTask(++_seq, $"task-{_seq}",
                draft.TenantId, draft.ApplicationId, draft.Kind,
                draft.Title, draft.Priority, draft.SkillTags ?? [],
                draft.DueUtc, TaskState.Open, draft.BlockingEntity,
                draft.BlockingEntityId, null,
                "2026-01-01T00:00:00.000Z", null, null,
                "2026-01-01T00:00:00.000Z");
            Store.Add(task);
            return Task.FromResult(task);
        }
        public Task<QueueTask?> GetAsync(string taskId,
            CancellationToken ct = default)
            => Task.FromResult(Store.FirstOrDefault(
                t => t.TaskId == taskId));
        public Task<IReadOnlyList<QueueTask>> ListQueueAsync(
            string tenantId, IReadOnlyList<string>? skills = null,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<QueueTask>>(Store
                .Where(t => t.TenantId == tenantId
                    && t.State is TaskState.Open or TaskState.Claimed)
                .ToList());
        public Task<QueueTask?> ClaimAsync(string taskId, string actor,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<QueueTask?> CompleteAsync(string taskId,
            string actor, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> ResolveBySourceAsync(string tenantId,
            string applicationId, string blockingEntity,
            string blockingEntityId, CancellationToken ct = default)
            => Task.FromResult(Store.RemoveAll(t =>
                t.TenantId == tenantId
                && t.ApplicationId == applicationId
                && t.BlockingEntity == blockingEntity
                && (t.BlockingEntityId ?? "") == blockingEntityId
                && t.State is TaskState.Open or TaskState.Claimed));
    }

    private sealed class FakeEvents : ILoanEventLog
    {
        public List<LoanEventDraft> Drafts { get; } = [];
        public Task WriteAsync(LoanEventDraft draft,
            CancellationToken ct = default)
        { Drafts.Add(draft); return Task.CompletedTask; }
        public Task<EventVerifyResult> VerifyChainAsync(
            CancellationToken ct = default)
            => Task.FromResult(
                new EventVerifyResult(true, null, Drafts.Count));
        public Task<IReadOnlyList<LoanEvent>> QueryAsync(
            string applicationId, int limit = 200,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LoanEvent>>([]);
    }

    private sealed class FakeRegistry : ITenantRegistry
    {
        public Task<TenantRecord?> FindByIdAsync(string id,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<TenantRecord?> FindBySlugAsync(string slug,
            CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TenantRecord>> ListAsync(
            CancellationToken ct) => throw new NotImplementedException();
        public Task InsertProvisioningAsync(string id, string slug,
            string d, string p, CancellationToken ct)
            => throw new NotImplementedException();
        public Task SetDbKeyAsync(string tenantId, string k,
            CancellationToken ct) => throw new NotImplementedException();
        public Task SetStatusAsync(string tenantId, string s,
            CancellationToken ct) => throw new NotImplementedException();
        public Task SetIspprIdsAsync(string t, string c, string cu,
            CancellationToken ct) => throw new NotImplementedException();
        public Task SetIspprKeyAsync(string t, string k, string w,
            CancellationToken ct) => throw new NotImplementedException();
    }
}
