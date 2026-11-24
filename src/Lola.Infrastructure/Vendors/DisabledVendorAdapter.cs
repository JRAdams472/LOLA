using Lola.Application.Errors;
using Lola.Application.Vendors;

namespace Lola.Infrastructure.Vendors;

/// Fail-closed IVendorAdapter for VENDOR_ADAPTER=disabled (MPE-277):
/// no real vendor integration exists yet, so Production requires the
/// explicit disabled value — every call surfaces a 503 rather than
/// simulated verification results.
public sealed class DisabledVendorAdapter : IVendorAdapter
{
    public string Adapter => "disabled";

    public IReadOnlySet<VendorKind> Kinds =>
        new HashSet<VendorKind>();

    public Task<VendorReceipt> SubmitAsync(VendorOrderRequest order,
        VendorCredentials? creds, CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "vendor capability is disabled by configuration");

    public Task<VendorResult?> PollAsync(VendorReceipt receipt,
        VendorCredentials? creds, CancellationToken ct = default)
        => throw DomainException.Unavailable(
            "vendor capability is disabled by configuration");
}
