using Lola.Application.Tenancy;
using Lola.Application.Vendors;

namespace Lola.Api.Tests;

/// In-memory vendor store — mirrors the real repo's semantics (hash
/// dedup, due-row filter, submit/complete/DLQ transitions, step
/// events) so the API e2e and the drain's retry logic run
/// hermetically. Rows carry the enqueuing TenantContext like the
/// real per-tenant files.
public sealed class FakeVendorOrderRepository : IVendorOrderRepository
{
    public Dictionary<string, VendorOrderRow> Orders { get; } = new();
    public Dictionary<string, VendorOutboxRow> Outbox { get; } = new();
    public List<(string AppId, string Step, string Event)> StepEvents
    { get; } = [];
    private readonly Dictionary<string, string> _byHash = new();
    private readonly Dictionary<string, string?> _outboxTenant = new();
    private readonly Dictionary<string, string> _orderOutbox = new();

    public Task<VendorEnqueueResult> EnqueueAsync(VendorOrderRow order,
        string outboxId, CancellationToken ct = default)
    {
        if (_byHash.TryGetValue(order.RequestHash, out var existing))
            return Task.FromResult(
                new VendorEnqueueResult(existing, true));
        order = order with
        {
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        Orders[order.OrderId] = order;
        Outbox[outboxId] = new VendorOutboxRow(
            outboxId, order.OrderId, order.RequestHash, "pending",
            0, null, null);
        _orderOutbox[order.OrderId] = outboxId;
        _outboxTenant[outboxId] = TenantContext.Current?.TenantId;
        _byHash[order.RequestHash] = order.OrderId;
        return Task.FromResult(
            new VendorEnqueueResult(order.OrderId, false));
    }

    public Task<VendorOrderRow?> GetAsync(string orderId,
        CancellationToken ct = default)
        => Task.FromResult(Orders.GetValueOrDefault(orderId));

    public Task<VendorOrderRow?> FindByExternalIdAsync(string kind,
        string externalId, CancellationToken ct = default)
        => Task.FromResult(Orders.Values.FirstOrDefault(
            o => o.Kind == kind && o.ExternalId == externalId));

    public Task<IReadOnlyList<VendorOrderRow>> ListByApplicationAsync(
        string applicationId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VendorOrderRow>>(
            Orders.Values.Where(o => o.ApplicationId == applicationId)
                .OrderBy(o => o.CreatedUtc ?? o.OrderId).ToList());

    public Task MarkSubmittedAsync(string orderId, string externalId,
        string outboxId, string? pollAfterUtc,
        CancellationToken ct = default)
    {
        Orders[orderId] = Orders[orderId] with
        {
            Status = "submitted",
            ExternalId = externalId,
            SubmittedUtc = DateTimeOffset.UtcNow.ToString("O")
        };
        Outbox[outboxId] = pollAfterUtc is not null
            ? Outbox[outboxId] with
            {
                Attempts = 0,
                NextRetryUtc = pollAfterUtc
            }
            : Outbox[outboxId] with { Status = "done" };
        return Task.CompletedTask;
    }

    public Task<VendorOutboxRow?> FindOutboxByOrderAsync(string orderId,
        CancellationToken ct = default)
        => Task.FromResult(Outbox.Values.FirstOrDefault(
            r => r.OrderId == orderId));

    public Task CompleteAsync(string orderId, string appId,
        string resultJson, string outboxId, string outcome,
        string step, string actor, CancellationToken ct = default)
    {
        var order = Orders[orderId];
        if (order.Status is "ordered" or "submitted")
        {
            Orders[orderId] = order with
            {
                Status = outcome,
                ResultJson = resultJson,
                CompletedUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            StepEvents.Add((appId, step, outcome));
        }
        if (Outbox.TryGetValue(outboxId, out var ob) &&
            ob.Status == "pending")
            Outbox[outboxId] = ob with { Status = "done" };
        return Task.CompletedTask;
    }

    public Task FailAsync(string orderId, string appId, string error,
        string outboxId, long attempts, string step, string actor,
        CancellationToken ct = default)
    {
        var order = Orders[orderId];
        if (order.Status is "ordered" or "submitted")
        {
            Orders[orderId] = order with
            {
                Status = "failed",
                Error = error,
                CompletedUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            StepEvents.Add((appId, step, "failed"));
        }
        if (Outbox.TryGetValue(outboxId, out var ob) &&
            ob.Status == "pending")
            Outbox[outboxId] = ob with
            { Status = "dlq", Attempts = attempts, LastError = error };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VendorOutboxRow>> ListDueOutboxAsync(
        string nowUtc, int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<VendorOutboxRow>>(
            Outbox.Values.Where(r => r.Status == "pending" &&
                _outboxTenant.GetValueOrDefault(r.OutboxId) ==
                    TenantContext.Current?.TenantId &&
                (r.NextRetryUtc is null ||
                 string.CompareOrdinal(r.NextRetryUtc, nowUtc) <= 0))
                .Take(limit).ToList());

    public Task MarkOutboxRetryAsync(string outboxId, long attempts,
        string nextRetryUtc, string? error, CancellationToken ct = default)
    {
        Outbox[outboxId] = Outbox[outboxId] with
        {
            Attempts = attempts,
            NextRetryUtc = nextRetryUtc,
            LastError = error
        };
        return Task.CompletedTask;
    }

    public Task MarkOutboxDlqAsync(string outboxId, long attempts,
        string error, CancellationToken ct = default)
    {
        Outbox[outboxId] = Outbox[outboxId] with
        {
            Status = "dlq",
            Attempts = attempts,
            LastError = error
        };
        return Task.CompletedTask;
    }

    /// Test seam — the outbox id paired with an order (the repo
    /// tracks it internally; tests correlate to assert states).
    public string? OutboxIdFor(string orderId)
        => _orderOutbox.GetValueOrDefault(orderId);
}
