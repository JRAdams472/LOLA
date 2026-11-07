using System.Text.Json.Nodes;
using Lola.Application.Documents;
using Lola.Application.Identity;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-190 outbox two-phase semantics through DocOutboxProcessor:
/// generate returns non-terminal → doc goes `generating` with the
/// vendor request id stamped + a near poll; the next drain polls
/// status → completed stamps filename. Non-retryable adapter
/// failures DLQ on first throw; retryable ones reschedule.
public sealed class DocWorkerTests
{
    private static readonly TenantRecord Tenant =
        new("t1", "t1", "T", "active", "x.db", "vault:v1:k",
            "2026-01-01T00:00:00Z");

    private static (Lola.Api.Documents.DocOutboxProcessor processor,
        FakeDocumentRepository repo) Sut(IDocumentService adapter)
    {
        var repo = new FakeDocumentRepository();
        var services = new ServiceCollection()
            .AddSingleton<IDocumentRepository>(repo)
            .AddSingleton(adapter)
            .BuildServiceProvider();
        return (new Lola.Api.Documents.DocOutboxProcessor(
            services, new FakeRegistry(), TimeProvider.System), repo);
    }

    private static async Task EnqueueAsync(FakeDocumentRepository repo)
    {
        var request = new DocPackageRequest("cust-1", "pkg", "doc-1",
            [new DocPackageDocument("t", new JsonObject())]);
        // Enqueue always runs inside a request's tenant scope in the
        // real flow — the fake stamps rows with TenantContext so the
        // drain's per-tenant filter sees them.
        using var _ = Lola.Application.Tenancy.TenantContext.Use(
            Lola.Application.Tenancy.TenantScope.From(Tenant));
        await repo.EnqueueAsync(
            new DocRecord("doc-1", "app-1", "le-package", "pkg",
                "x", null, null, "pending", null, "actor",
                null, null, "h1"),
            "ob-1", "generate", DocWire.PayloadJson(request));
    }

    [Fact]
    public async Task Generate_then_poll_completes_with_filename()
    {
        var (processor, repo) = Sut(new ScriptedAdapter(
            new DocGeneration("req-1", "generating", null),
            new DocGeneration("req-1", "completed", "pkg_req-1.zip")));
        await EnqueueAsync(repo);

        await processor.DrainTenantAsync(Tenant);
        repo.Docs["doc-1"].Status.ShouldBe("generating");
        repo.Docs["doc-1"].RequestId.ShouldBe("req-1");

        // Simulate the poll interval elapsing — the worker
        // rescheduled the row 5s out after submission.
        repo.Outbox["ob-1"] = repo.Outbox["ob-1"] with
        { NextRetryUtc = null };
        await processor.DrainTenantAsync(Tenant);
        repo.Docs["doc-1"].Status.ShouldBe("completed");
        repo.Docs["doc-1"].Filename.ShouldBe("pkg_req-1.zip");
        repo.Outbox["ob-1"].Status.ShouldBe("done");
    }

    [Fact]
    public async Task Immediate_completed_skips_the_poll_phase()
    {
        var (processor, repo) = Sut(new ScriptedAdapter(
            new DocGeneration("req-9", "completed", "pkg.zip")));
        await EnqueueAsync(repo);
        await processor.DrainTenantAsync(Tenant);
        repo.Docs["doc-1"].Status.ShouldBe("completed");
        repo.Docs["doc-1"].Filename.ShouldBe("pkg.zip");
    }

    [Fact]
    public async Task Nonretryable_error_dlqs_on_first_throw()
    {
        var (processor, repo) = Sut(new ScriptedAdapter(
            DocAdapterException.Auth("bad api key")));
        await EnqueueAsync(repo);
        await processor.DrainTenantAsync(Tenant);
        repo.Outbox["ob-1"].Status.ShouldBe("dlq");
        repo.Docs["doc-1"].Status.ShouldBe("failed");
    }

    [Fact]
    public async Task Retryable_error_reschedules_with_backoff()
    {
        var (processor, repo) = Sut(new ScriptedAdapter(
            DocAdapterException.Unavailable("isppr down")));
        await EnqueueAsync(repo);
        await processor.DrainTenantAsync(Tenant);
        repo.Outbox["ob-1"].Status.ShouldBe("pending");
        repo.Outbox["ob-1"].Attempts.ShouldBe(1);
        repo.Outbox["ob-1"].NextRetryUtc.ShouldNotBeNull();
    }

    private sealed class ScriptedAdapter(params object[] script)
        : IDocumentService
    {
        private readonly Queue<object> _script = new(script);
        public string Adapter => "scripted";
        private object Next()
            => _script.Count > 0 ? _script.Dequeue()
                : new DocGeneration("req-1", "completed", "x.zip");

        private Task<DocGeneration> Result()
            => Next() switch
            {
                Exception ex => throw ex,
                DocGeneration g => Task.FromResult(g),
                _ => throw new InvalidOperationException("bad script")
            };

        public Task<DocGeneration> GenerateAsync(
            DocPackageRequest request, CancellationToken ct = default)
            => Result();
        public Task<DocGeneration> GetStatusAsync(string requestId,
            CancellationToken ct = default)
            => Result();
        public Task<string> GetDownloadUrlAsync(string requestId,
            CancellationToken ct = default)
            => Task.FromResult($"https://x/{requestId}");
    }

    private sealed class FakeRegistry : ITenantRegistry
    {
        public Task<TenantRecord?> FindByIdAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TenantRecord?> FindBySlugAsync(string slug, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task InsertProvisioningAsync(string id, string slug, string d, string p, CancellationToken ct) => throw new NotImplementedException();
        public Task SetDbKeyAsync(string tenantId, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task SetStatusAsync(string tenantId, string s, CancellationToken ct) => throw new NotImplementedException();
        public Task SetIspprIdsAsync(string t, string c, string cu, CancellationToken ct) => throw new NotImplementedException();
        public Task SetIspprKeyAsync(string t, string k, string w, CancellationToken ct) => throw new NotImplementedException();
    }
}
