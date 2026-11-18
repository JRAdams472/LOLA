namespace Lola.Application.Vendors;

/// Typed adapter failures — the outbox processor classifies retry
/// policy off the kind, not the message (same contract as
/// FeeAdapterException). `Rejected`/`Contract` never heal on retry,
/// so they DLQ immediately; `Unavailable` gets backoff retries;
/// `Auth` is retryable once — a rotated credential may already be
/// re-seeded in OpenBao KV by an external process.
public enum VendorErrorKind { Auth, Rejected, Unavailable, Contract }

public sealed class VendorAdapterException(
    VendorErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public VendorErrorKind Kind { get; } = kind;
    public bool Retryable => Kind is VendorErrorKind.Unavailable
        or VendorErrorKind.Auth;
}
