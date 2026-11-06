namespace Lola.Application.Fees;

/// Typed adapter failures (MPE-183) — the outbox worker classifies
/// retry policy off the kind, not the message. `Rejected`/`Contract`
/// will never heal on retry, so they DLQ immediately; `Unavailable`
/// gets backoff retries; `Auth` is retryable once (a stale token
/// file may be re-minted by an external process) then terminal.
public enum FeeErrorKind { Auth, Rejected, Unavailable, Contract }

public sealed class FeeAdapterException(
    FeeErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public FeeErrorKind Kind { get; } = kind;
    public bool Retryable => Kind is FeeErrorKind.Unavailable;
}
