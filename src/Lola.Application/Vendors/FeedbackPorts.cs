namespace Lola.Application.Vendors;

/// Tenant-file vendor feedback ledger port (M17). The ledger is the
/// dedupe + receipt record for signed validation webhooks — feedback
/// has no order row to correlate on, so `(source, feedback_id)` is the
/// replay key. Implementation lives in the data plane; handlers see
/// only this surface.
public interface IVendorFeedbackRepository
{
    /// Insert the receipt row. Returns false when `(source,
    /// feedback_id)` already exists — a replay; the caller treats
    /// that as a 200 no-op and performs no second write.
    Task<bool> TryRecordAsync(VendorFeedbackReceipt receipt,
        CancellationToken ct = default);

    Task<VendorFeedbackRow?> GetAsync(string source, string feedbackId,
        CancellationToken ct = default);

    Task<IReadOnlyList<VendorFeedbackRow>> ListByApplicationAsync(
        string applicationId, CancellationToken ct = default);
}

/// What the ingress knows at record time — the contract plus the
/// receive metadata. Every member is metadata; the PII firewall
/// scans this record with the rest of the namespace.
public sealed record VendorFeedbackReceipt(
    string Source, string ApplicationId, string FeedbackId,
    string Finding, string Severity,
    string? LinkedEntity, string? LinkedEntityId);

/// `vendor_feedback` row — PII-free metadata by construction; the
/// table has no value-bearing columns.
public sealed record VendorFeedbackRow(
    string Source, string ApplicationId, string FeedbackId,
    string Finding, string Severity,
    string? LinkedEntity, string? LinkedEntityId,
    string ReceivedUtc);
