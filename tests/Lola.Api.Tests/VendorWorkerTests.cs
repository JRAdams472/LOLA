using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Application.Vendors;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-227 outbox retry policy through VendorOutboxProcessor:
/// transient adapter failures reschedule with backoff and
/// dead-letter at MaxAttempts (which also fails the order and
/// writes the 'failed' service-step event); non-retryable failures
/// DLQ on the first throw. Credentials resolve per kind from the
/// credential source — the drain proves it looks them up.
public sealed class VendorWorkerTests
{
    private static readonly TenantRecord Tenant =
        new("t1", "t1", "T", "active", "x.db", "vault:v1:k",
            "2026-01-01T00:00:00Z");

    private static (
        Lola.Api.Vendors.VendorOutboxProcessor processor,
        FakeVendorOrderRepository repo,
        FakeVendorCredentialSource creds) Sut(IVendorAdapter adapter,
        Lola.Application.Security.IFieldCipher? cipher = null)
    {
        var repo = new FakeVendorOrderRepository();
        var creds = new FakeVendorCredentialSource();
        var services = new ServiceCollection()
            .AddSingleton<IVendorOrderRepository>(repo)
            .AddSingleton(adapter)
            .AddSingleton<IVendorCredentialSource>(creds)
            .AddSingleton<Lola.Application.Security.IFieldCipher>(
                cipher ?? new LoopbackFieldCipher())
            .AddSingleton<Lola.Application.Audit.ITenantAuditLog>(
                new FakeTenantAuditLog())
            .BuildServiceProvider();
        return (new Lola.Api.Vendors.VendorOutboxProcessor(
            services, new FakeRegistry(), TimeProvider.System),
            repo, creds);
    }

    private static async Task EnqueueAsync(
        FakeVendorOrderRepository repo)
    {
        using var _ = TenantContext.Use(
            new TenantScope("t1", "x.db", "k"));
        var request = new CreditOrderRequest(
            "Ada", "Testcase", "123456789", "1985-04-12",
            "42 Main St", "Springfield", "IL", "62701");
        var json = VendorRequestMapper.RequestJson(request);
        var order = new VendorOrderRow(
            "ord-1", "app-1", "credit", "x", null, "ordered",
            $"vault:loop:{Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(json))}",
            null, null, "officer-1", null, null, null,
            VendorRequestMapper.RequestHash("t1", "app-1",
                VendorKind.Credit, json));
        await repo.EnqueueAsync(order, "ob-1");
    }

    [Fact]
    public async Task Stub_submit_then_poll_completes_the_lifecycle()
    {
        var (processor, repo, creds) = Sut(new AltStubVendorAdapter());
        await EnqueueAsync(repo);

        (await processor.DrainTenantAsync(Tenant)).ShouldBe(1);

        var order = repo.Orders["ord-1"];
        order.Status.ShouldBe("completed");
        order.ExternalId.ShouldStartWith("alt-credit-");
        order.ResultJson.ShouldStartWith("vault:");
        repo.Outbox["ob-1"].Status.ShouldBe("done");
        repo.StepEvents.ShouldContain(
            e => e.AppId == "app-1" && e.Step == "Credit" &&
                e.Event == "completed");
        creds.Lookups.ShouldBe([VendorKind.Credit]);
    }

    /// MPE-229 regression: the dev drain DLQ'd orders with "No tenant
    /// in CipherContext" — TransitFieldCipher reads CipherContext for
    /// its key name and the processor only scoped TenantContext. A
    /// cipher that enforces the same contract proves the drain sets
    /// both contexts.
    [Fact]
    public async Task Drain_scopes_cipher_context_for_field_crypto()
    {
        var (processor, repo, _) = Sut(new AltStubVendorAdapter(),
            new CipherContextCipher());
        await EnqueueAsync(repo);

        (await processor.DrainTenantAsync(Tenant)).ShouldBe(1);
        repo.Orders["ord-1"].Status.ShouldBe("completed");
    }

    [Fact]
    public async Task Pending_poll_keeps_the_outbox_as_poll_handle()
    {
        var (processor, repo, _) = Sut(new PendingAdapter());
        await EnqueueAsync(repo);

        await processor.DrainTenantAsync(Tenant);

        var order = repo.Orders["ord-1"];
        order.Status.ShouldBe("submitted");
        order.ExternalId.ShouldBe("ext-1");
        order.ResultJson.ShouldBeNull();
        // The pending row is now the poll handle — scheduled for the
        // next tick with a fresh attempt budget (MPE-228).
        var row = repo.Outbox["ob-1"];
        row.Status.ShouldBe("pending");
        row.NextRetryUtc.ShouldNotBeNull();
        row.Attempts.ShouldBe(0);
        // No completion event until a channel lands the result.
        repo.StepEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Deferred_poll_completes_on_a_later_drain()
    {
        var (processor, repo, _) = Sut(new DeferredPollAdapter(1));
        await EnqueueAsync(repo);

        // First drain: submit + null poll → submitted, poll scheduled.
        await processor.DrainTenantAsync(Tenant);
        repo.Orders["ord-1"].Status.ShouldBe("submitted");
        repo.Outbox["ob-1"].Status.ShouldBe("pending");

        // Second due drain: poll answers → completed atomically.
        repo.Outbox["ob-1"] = repo.Outbox["ob-1"]
            with
        { NextRetryUtc = null };
        await processor.DrainTenantAsync(Tenant);

        var order = repo.Orders["ord-1"];
        order.Status.ShouldBe("completed");
        order.ResultJson.ShouldStartWith("vault:");
        repo.Outbox["ob-1"].Status.ShouldBe("done");
        repo.StepEvents.ShouldContain(
            e => e.AppId == "app-1" && e.Step == "Credit" &&
                e.Event == "completed");
    }

    [Fact]
    public async Task Push_adapter_closes_the_outbox_at_submit()
    {
        var (processor, repo, _) = Sut(new PushAdapter());
        await EnqueueAsync(repo);

        await processor.DrainTenantAsync(Tenant);

        repo.Orders["ord-1"].Status.ShouldBe("submitted");
        // No poll handle for push — the webhook is the channel.
        repo.Outbox["ob-1"].Status.ShouldBe("done");
    }

    [Fact]
    public async Task Transient_failure_reschedules_then_dlqs_and_fails()
    {
        var (processor, repo, _) = Sut(new ScriptedAdapter(
            new VendorAdapterException(VendorErrorKind.Unavailable,
                "vendor 503")));
        await EnqueueAsync(repo);

        for (var i = 1; i <= 4; i++)
        {
            repo.Outbox["ob-1"] = repo.Outbox["ob-1"]
                with
            { NextRetryUtc = null };
            await processor.DrainTenantAsync(Tenant);
            var row = repo.Outbox["ob-1"];
            row.Attempts.ShouldBe(i);
            row.Status.ShouldBe("pending");
            row.LastError!.ShouldContain("Unavailable");
            row.NextRetryUtc.ShouldNotBeNull();
        }

        repo.Outbox["ob-1"] = repo.Outbox["ob-1"]
            with
        { NextRetryUtc = null };
        await processor.DrainTenantAsync(Tenant);
        repo.Outbox["ob-1"].Status.ShouldBe("dlq");
        repo.Outbox["ob-1"].Attempts.ShouldBe(5);
        repo.Orders["ord-1"].Status.ShouldBe("failed");
        repo.StepEvents.ShouldContain(
            e => e.Step == "Credit" && e.Event == "failed");
    }

    [Fact]
    public async Task Non_retryable_failure_dlqs_immediately()
    {
        var (processor, repo, _) = Sut(new ScriptedAdapter(
            new VendorAdapterException(VendorErrorKind.Rejected,
                "vendor refused")));
        await EnqueueAsync(repo);

        await processor.DrainTenantAsync(Tenant);

        repo.Outbox["ob-1"].Status.ShouldBe("dlq");
        repo.Outbox["ob-1"].Attempts.ShouldBe(1); // no retry burn
        repo.Orders["ord-1"].Status.ShouldBe("failed");
        repo.Orders["ord-1"].Error!.ShouldContain("Rejected");
    }

    private sealed class ScriptedAdapter(Exception ex) : IVendorAdapter
    {
        public string Adapter => "fails";
        public IReadOnlySet<VendorKind> Kinds { get; } =
            new HashSet<VendorKind> { VendorKind.Credit };
        public Task<VendorReceipt> SubmitAsync(VendorOrderRequest o,
            VendorCredentials? c, CancellationToken ct = default)
            => Task.FromException<VendorReceipt>(ex);
        public Task<VendorResult?> PollAsync(VendorReceipt r,
            VendorCredentials? c, CancellationToken ct = default)
            => Task.FromResult<VendorResult?>(null);
    }

    private sealed class PendingAdapter : IVendorAdapter
    {
        public string Adapter => "pending";
        public IReadOnlySet<VendorKind> Kinds { get; } =
            new HashSet<VendorKind> { VendorKind.Credit };
        public Task<VendorReceipt> SubmitAsync(VendorOrderRequest o,
            VendorCredentials? c, CancellationToken ct = default)
            => Task.FromResult(new VendorReceipt("ext-1", "now"));
        public Task<VendorResult?> PollAsync(VendorReceipt r,
            VendorCredentials? c, CancellationToken ct = default)
            => Task.FromResult<VendorResult?>(null);
    }

    /// Poll answers null `empties` times, then the result — the
    /// rescheduled-drain proof.
    private sealed class DeferredPollAdapter(int empties)
        : IVendorAdapter
    {
        private int _left = empties;
        public string Adapter => "deferred";
        public IReadOnlySet<VendorKind> Kinds { get; } =
            new HashSet<VendorKind> { VendorKind.Credit };
        public Task<VendorReceipt> SubmitAsync(VendorOrderRequest o,
            VendorCredentials? c, CancellationToken ct = default)
            => Task.FromResult(new VendorReceipt("ext-1", "now"));
        public Task<VendorResult?> PollAsync(VendorReceipt r,
            VendorCredentials? c, CancellationToken ct = default)
            => Task.FromResult(_left-- > 0 ? null : new VendorResult(
                VendorKind.Credit, "completed", null,
                new System.Text.Json.Nodes.JsonObject
                { ["external_id"] = r.ExternalId }));
    }

    /// Push-capable — the drain must not schedule polls.
    private sealed class PushAdapter : IVendorAdapter
    {
        public string Adapter => "push";
        public bool SupportsPush => true;
        public IReadOnlySet<VendorKind> Kinds { get; } =
            new HashSet<VendorKind> { VendorKind.Credit };
        public Task<VendorReceipt> SubmitAsync(VendorOrderRequest o,
            VendorCredentials? c, CancellationToken ct = default)
            => Task.FromResult(new VendorReceipt("ext-1", "now"));
        public Task<VendorResult?> PollAsync(VendorReceipt r,
            VendorCredentials? c, CancellationToken ct = default)
            => Task.FromResult<VendorResult?>(null);
    }

    /// Enforces the real TransitFieldCipher contract: every crypto op
    /// requires CipherContext. LoopbackFieldCipher can't catch a
    /// missing scope — this can.
    private sealed class CipherContextCipher
        : Lola.Application.Security.IFieldCipher
    {
        private readonly LoopbackFieldCipher _inner = new();

        public Task<string> EncryptAsync(string plaintext,
            CancellationToken ct = default)
        {
            _ = Lola.Application.Security.CipherContext
                .RequiredTenantId;
            return _inner.EncryptAsync(plaintext, ct);
        }

        public Task<IReadOnlyList<string>> EncryptAsync(
            IReadOnlyList<string> plaintexts,
            CancellationToken ct = default)
        {
            _ = Lola.Application.Security.CipherContext
                .RequiredTenantId;
            return _inner.EncryptAsync(plaintexts, ct);
        }

        public Task<string> DecryptAsync(string ciphertext,
            CancellationToken ct = default)
        {
            _ = Lola.Application.Security.CipherContext
                .RequiredTenantId;
            return _inner.DecryptAsync(ciphertext, ct);
        }

        public Task<IReadOnlyList<string>> DecryptAsync(
            IReadOnlyList<string> ciphertexts,
            CancellationToken ct = default)
        {
            _ = Lola.Application.Security.CipherContext
                .RequiredTenantId;
            return _inner.DecryptAsync(ciphertexts, ct);
        }
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
