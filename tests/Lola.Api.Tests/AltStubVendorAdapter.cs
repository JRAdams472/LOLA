using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lola.Application.Vendors;

namespace Lola.Api.Tests;

/// The second IVendorAdapter implementation (MPE-227) — the e2e
/// runs on it, proving the swap is interface-level. Captures the
/// exact requests crossing the egress boundary; completes
/// immediately like the stub, tagged `alt-stub`.
public sealed class AltStubVendorAdapter : IVendorAdapter
{
    public System.Collections.Concurrent.ConcurrentQueue<VendorOrderRequest>
        Requests
    { get; } = new();

    public string Adapter => "alt-stub";

    public IReadOnlySet<VendorKind> Kinds { get; } =
        new HashSet<VendorKind>
        {
            VendorKind.Credit, VendorKind.Voe, VendorKind.Voi,
            VendorKind.Flood, VendorKind.Fraud, VendorKind.Aus
        };

    public Task<VendorReceipt> SubmitAsync(VendorOrderRequest order,
        VendorCredentials? creds, CancellationToken ct = default)
    {
        Requests.Enqueue(order);
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(order))))
            [..12].ToLowerInvariant();
        return Task.FromResult(new VendorReceipt(
            $"alt-{VendorGuards.Token(order.Kind)}-{hash}",
            DateTime.UtcNow.ToString("O")));
    }

    public Task<VendorResult?> PollAsync(VendorReceipt receipt,
        VendorCredentials? creds, CancellationToken ct = default)
        => Task.FromResult<VendorResult?>(new VendorResult(
            VendorGuards.Parse(
                receipt.ExternalId.Split('-')[1]),
            "completed", null, new JsonObject
            {
                ["disposition"] = "alt-stub-complete",
                ["external_id"] = receipt.ExternalId
            }));
}

/// Vendor credential source for the hermetic API e2e — returns a
/// recognizable test binding; the real KV-only path is proven in
/// Lola.Infrastructure.Tests against live OpenBao.
public sealed class FakeVendorCredentialSource : IVendorCredentialSource
{
    public List<VendorKind> Lookups { get; } = [];

    public Task<VendorCredentials?> ForAsync(VendorKind kind,
        CancellationToken ct = default)
    {
        Lookups.Add(kind);
        return Task.FromResult<VendorCredentials?>(
            new VendorCredentials("test-api-key", "test-webhook-secret"));
    }
}
