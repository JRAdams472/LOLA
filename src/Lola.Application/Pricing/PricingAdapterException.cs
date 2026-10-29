namespace Lola.Application.Pricing;

/// Typed adapter failures (MPE-130) — the outbox worker classifies
/// retry policy off the kind, not the message. `Rejected`/`Contract`
/// will never heal on retry, so they DLQ immediately; `Unavailable`
/// gets backoff retries; `Auth` is retryable once (a stale cached
/// token) then terminal.
public enum PricingErrorKind { Auth, Rejected, Unavailable, Contract }

public sealed class PricingAdapterException(
    PricingErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public PricingErrorKind Kind { get; } = kind;
    public bool Retryable => Kind is PricingErrorKind.Unavailable;
}
