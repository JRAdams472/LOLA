namespace Lola.Application.Events;

/// Append-only mutation log inside the tenant's SQLCipher file (M15).
/// Values arrive already encoded — LoanEventWriter turns PII-classed
/// before/after into blind-index HMACs upstream (never plaintext).
/// The implementation chains the row to the previous hash and writes
/// it inside the caller's atomic unit. Rows are never updated or
/// deleted.
public interface ILoanEventLog
{
    /// Write one chained event row. The caller's write transaction must
    /// be open — atomicity with the mutation is the contract.
    Task WriteAsync(LoanEventDraft draft, CancellationToken ct = default);
    Task<EventVerifyResult> VerifyChainAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LoanEvent>> QueryAsync(string applicationId,
        int limit = 200, CancellationToken ct = default);
}
