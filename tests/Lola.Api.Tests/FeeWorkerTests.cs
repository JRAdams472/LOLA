using Lola.Application.Fees;
using Lola.Application.Identity;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-183 outbox retry policy through FeeOutboxProcessor:
/// transient adapter failures reschedule with backoff and
/// dead-letter at MaxAttempts; non-retryable failures DLQ on the
/// first throw.
public sealed class FeeWorkerTests
{
    private static readonly TenantRecord Tenant =
        new("t1", "t1", "T", "active", "x.db", "vault:v1:k",
            "2026-01-01T00:00:00Z");

    private static (
        Lola.Api.Fees.FeeOutboxProcessor processor,
        FakeFeeRepository repo) Sut(IFeeService adapter)
    {
        var repo = new FakeFeeRepository();
        var services = new ServiceCollection()
            .AddSingleton<IFeeRepository>(repo)
            .AddSingleton(adapter)
            .BuildServiceProvider();
        return (new Lola.Api.Fees.FeeOutboxProcessor(
            services, new FakeRegistry(), TimeProvider.System), repo);
    }

    private static async Task EnqueueAsync(FakeFeeRepository repo)
    {
        // The fake scopes rows by TenantContext like a tenant file —
        // enqueue inside t1's scope so DrainTenantAsync(t1) sees it.
        using var _ = Lola.Application.Tenancy.TenantContext.Use(
            new Lola.Application.Tenancy.TenantScope("t1", "x.db", "k"));
        await repo.EnqueueAsync(
            new FeeEvalRecord("eval-1", "app-1", "x",
                "h1", "{}", "pending", null, null, "actor", null, null),
            "ob-1", "eval",
            FeeWire.PayloadJson(Scenario, new FeeRequestOptions()));
    }

    private static readonly FeeScenario Scenario = new(
        new FeeLoanSection(null, null, null, null),
        new FeePropertySection(null, null, null, null, null),
        new FeeBorrowerSection(null, null, null));

    [Fact]
    public async Task Transient_failure_reschedules_then_dlqs()
    {
        var (processor, repo) = Sut(new ScriptedAdapter(
            new FeeAdapterException(FeeErrorKind.Unavailable,
                "Zara 503")));
        await EnqueueAsync(repo);

        // Attempts 1–4 reschedule with a future retry instant.
        for (var i = 1; i <= 4; i++)
        {
            // Force the row due again — backoff is in the future.
            var row = repo.Outbox["ob-1"];
            repo.Outbox["ob-1"] = row with { NextRetryUtc = null };
            await processor.DrainTenantAsync(Tenant);
            row = repo.Outbox["ob-1"];
            row.Attempts.ShouldBe(i);
            row.Status.ShouldBe("pending");
            row.LastError.ShouldNotBeNull();
            row.LastError.ShouldContain("Unavailable");
            row.NextRetryUtc.ShouldNotBeNull();
        }

        // Attempt 5 hits MaxAttempts → DLQ + failed eval.
        repo.Outbox["ob-1"] = repo.Outbox["ob-1"]
            with
        { NextRetryUtc = null };
        await processor.DrainTenantAsync(Tenant);
        repo.Outbox["ob-1"].Status.ShouldBe("dlq");
        repo.Outbox["ob-1"].Attempts.ShouldBe(5);
        repo.Evals["eval-1"].Status.ShouldBe("failed");
    }

    [Fact]
    public async Task Non_retryable_failure_dlqs_immediately()
    {
        var (processor, repo) = Sut(new ScriptedAdapter(
            new FeeAdapterException(FeeErrorKind.Rejected,
                "tenant mismatch")));
        await EnqueueAsync(repo);

        await processor.DrainTenantAsync(Tenant);

        repo.Outbox["ob-1"].Status.ShouldBe("dlq");
        repo.Outbox["ob-1"].Attempts.ShouldBe(1); // no retry burn
        repo.Evals["eval-1"].Status.ShouldBe("failed");
        repo.Evals["eval-1"].Error.ShouldNotBeNull();
        repo.Evals["eval-1"].Error!.ShouldContain("Rejected");
    }

    private sealed class ScriptedAdapter(Exception ex) : IFeeService
    {
        public string Adapter => "fails";
        public Task<FeeEvaluation> EvaluateAsync(
            FeeScenario s, FeeRequestOptions o,
            CancellationToken ct = default)
            => Task.FromException<FeeEvaluation>(ex);
    }

    private sealed class FakeRegistry : ITenantRegistry
    {
        public Task<TenantRecord?> FindByIdAsync(string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<TenantRecord?> FindBySlugAsync(string slug, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task InsertProvisioningAsync(string id, string slug, string d, string p, CancellationToken ct) => throw new NotImplementedException();
        public Task SetDbKeyAsync(string tenantId, string k, CancellationToken ct) => throw new NotImplementedException();
        public Task SetStatusAsync(string tenantId, string s, CancellationToken ct) => throw new NotImplementedException();
    }
}
